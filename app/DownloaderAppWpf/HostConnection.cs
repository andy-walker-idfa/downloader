using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DownloaderAppWpf;

/// <summary>
/// A single, long-lived connection to DownloaderHost.exe.
///
/// One process serves the whole app. The previous design started a host per download and closed
/// stdin straight after writing, which is what limited the app to one transfer at a time: closing
/// stdin is precisely what tells the host to exit. Requests are correlated by id, never by
/// assuming replies arrive in order.
/// </summary>
public sealed class HostConnection : IDisposable
{
    private sealed class Pending
    {
        public required TaskCompletionSource<HostMessage> Completion { get; init; }
        public Action<HostMessage>? OnInterim { get; init; }
    }

    private readonly ConcurrentDictionary<string, Pending> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _startLock = new();

    private Process? _process;
    private Stream? _stdin;
    private int _nextId;
    private bool _disposed;

    /// <summary>Replies that match no outstanding request. Informational; never throws.</summary>
    public event Action<HostMessage>? Unsolicited;

    /// <summary>The host exited or the pipe broke. The UI should say so rather than hang.</summary>
    public event Action<string>? Disconnected;

    public bool IsConnected => _process is { HasExited: false };

    // --- lifetime -------------------------------------------------------------

    private Process EnsureStarted()
    {
        lock (_startLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_process is { HasExited: false }) return _process;

            var startInfo = new ProcessStartInfo
            {
                FileName = HostLocator.Resolve(),
                // The host expects the caller origin as argv[0], exactly as a browser passes it.
                Arguments = "chrome-extension://" + NativeHostRegistrar.ExtensionId + "/",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start DownloaderHost.exe");

            _process = process;
            _stdin = process.StandardInput.BaseStream;

            _ = Task.Run(() => ReadLoopAsync(process));

            // Drain stderr so a full pipe can never block the host, and keep it for diagnosis.
            _ = Task.Run(async () =>
            {
                try
                {
                    var text = await process.StandardError.ReadToEndAsync();
                    if (!string.IsNullOrWhiteSpace(text)) Log("host stderr: " + text.Trim());
                }
                catch
                {
                    // The pipe closing at shutdown is normal.
                }
            });

            return process;
        }
    }

    // --- requests -------------------------------------------------------------

    /// <summary>Allocates an id and records the request so its replies can be routed back.</summary>
    private (string Id, Pending Pending) Register(Action<HostMessage>? onInterim)
    {
        var id = Interlocked.Increment(ref _nextId).ToString();
        var pending = new Pending
        {
            Completion = new TaskCompletionSource<HostMessage>(TaskCreationOptions.RunContinuationsAsynchronously),
            OnInterim = onInterim
        };
        _pending[id] = pending;
        return (id, pending);
    }

    /// <summary>
    /// Starts a download and hands back its id straight away, so it can be paused or cancelled
    /// while it runs. SendAsync hides the id, which left no way to name a specific transfer.
    /// </summary>
    public DownloadHandle StartDownload(object arguments, Action<HostMessage>? onInterim = null)
    {
        var (id, pending) = Register(onInterim);

        _ = WriteAsync(BuildPayload("download", id, arguments), CancellationToken.None)
            .ContinueWith(
                write =>
                {
                    if (write.IsFaulted && _pending.TryRemove(id, out var failed))
                    {
                        failed.Completion.TrySetException(write.Exception!.GetBaseException());
                    }
                },
                TaskScheduler.Default);

        return new DownloadHandle(this, id, pending.Completion.Task);
    }

    /// <summary>
    /// Sends a command and completes when its terminal reply arrives. Interim replies (started,
    /// progress) go to <paramref name="onInterim"/> and keep the request open.
    /// </summary>
    public async Task<HostMessage> SendAsync(
        string command,
        object? arguments = null,
        Action<HostMessage>? onInterim = null,
        CancellationToken cancellationToken = default)
    {
        var (id, pending) = Register(onInterim);

        try
        {
            await WriteAsync(BuildPayload(command, id, arguments), cancellationToken);
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }

        using var registration = cancellationToken.Register(() =>
        {
            if (_pending.TryRemove(id, out var p)) p.Completion.TrySetCanceled(cancellationToken);
        });

        return await pending.Completion.Task;
    }

    /// <summary>
    /// Sends a command that refers to another request, such as pause or cancel. The outcome
    /// arrives on the original request, so there is nothing to await here.
    /// </summary>
    public Task PostAsync(string command, object? arguments = null)
    {
        var id = Interlocked.Increment(ref _nextId).ToString();
        return WriteAsync(BuildPayload(command, id, arguments), CancellationToken.None);
    }

    private static byte[] BuildPayload(string command, string id, object? arguments)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("cmd", command);
            writer.WriteString("id", id);
            writer.WriteString("source", "app");

            if (arguments is not null)
            {
                foreach (var property in JsonSerializer.SerializeToElement(arguments).EnumerateObject())
                {
                    property.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private async Task WriteAsync(byte[] payload, CancellationToken cancellationToken)
    {
        EnsureStarted();

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var stream = _stdin ?? throw new InvalidOperationException("host stdin unavailable");
            await stream.WriteAsync(BitConverter.GetBytes(payload.Length).AsMemory(0, 4), cancellationToken);
            await stream.WriteAsync(payload.AsMemory(), cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // --- reading --------------------------------------------------------------

    private async Task ReadLoopAsync(Process process)
    {
        var stdout = process.StandardOutput.BaseStream;
        var reason = "host exited";

        try
        {
            while (true)
            {
                var header = new byte[4];
                if (!await ReadExactlyAsync(stdout, header)) break;

                var length = BitConverter.ToInt32(header, 0);
                if (length <= 0) break;

                var payload = new byte[length];
                if (!await ReadExactlyAsync(stdout, payload)) break;

                try
                {
                    using var document = JsonDocument.Parse(payload);
                    Dispatch(new HostMessage(document.RootElement.Clone()));
                }
                catch (JsonException ex)
                {
                    Log("unparseable reply from host: " + ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            reason = ex.Message;
        }

        // Whatever the cause, nothing is coming back for these. Failing them is what turns a
        // dead host into a visible error rather than a UI that waits for ever.
        foreach (var key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out var pending))
            {
                pending.Completion.TrySetException(new IOException("native host disconnected: " + reason));
            }
        }

        Log("read loop ended: " + reason);
        if (!_disposed) Disconnected?.Invoke(reason);
    }

    private void Dispatch(HostMessage message)
    {
        var id = message.Id;

        if (id is not null && _pending.TryGetValue(id, out var pending))
        {
            if (message.IsTerminal)
            {
                _pending.TryRemove(id, out _);
                pending.Completion.TrySetResult(message);
            }
            else
            {
                try
                {
                    pending.OnInterim?.Invoke(message);
                }
                catch (Exception ex)
                {
                    Log("interim handler threw: " + ex.Message);
                }
            }

            return;
        }

        try
        {
            Unsolicited?.Invoke(message);
        }
        catch (Exception ex)
        {
            Log("unsolicited handler threw: " + ex.Message);
        }
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset));
            if (read == 0) return false;
            offset += read;
        }

        return true;
    }

    private static void Log(string text)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsDownloader");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "app.log"),
                "[" + DateTimeOffset.Now.ToString("O") + "] " + text + Environment.NewLine);
        }
        catch
        {
            // Logging must never break the app.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Closing stdin is how the host is asked to exit, so it happens only here -- never
        // between downloads, which would kill transfers that are still running.
        try
        {
            _stdin?.Close();
        }
        catch
        {
            // Already gone.
        }

        try
        {
            if (_process is { HasExited: false } && !_process.WaitForExit(3000))
            {
                _process.Kill();
            }
        }
        catch
        {
            // Already gone.
        }

        _process?.Dispose();
        _writeLock.Dispose();
    }
}

/// <summary>
/// A running download. Pause and cancel refer to it by the id the connection assigned, which is
/// why starting a download has to hand that id back.
/// </summary>
public sealed class DownloadHandle
{
    private readonly HostConnection _connection;

    internal DownloadHandle(HostConnection connection, string id, Task<HostMessage> completion)
    {
        _connection = connection;
        Id = id;
        Completion = completion;
    }

    public string Id { get; }

    /// <summary>Completes with the terminal reply: finished, paused, cancelled or error.</summary>
    public Task<HostMessage> Completion { get; }

    /// <summary>Stops the transfer but keeps the partial file, so it can be resumed.</summary>
    public Task PauseAsync() => _connection.PostAsync("pause", new { target = Id });

    /// <summary>Stops the transfer and discards the partial file.</summary>
    public Task CancelAsync() => _connection.PostAsync("cancel", new { target = Id });
}
