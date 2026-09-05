using System.IO;
using System.Net;
using System.Text;
using Path = System.IO.Path;
using DownloaderAppWpf;
using Xunit;

namespace DownloaderAppWpf.Tests;

/// <summary>
/// Serves a file from localhost so these tests need no network and no external site.
/// </summary>
internal sealed class LocalFileServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly byte[] _body;
    private readonly CancellationTokenSource _cts = new();

    public string Url { get; }

    private readonly int _chunkDelayMs;

    public LocalFileServer(int port, int sizeBytes, string fileName, int chunkDelayMs = 0)
    {
        _chunkDelayMs = chunkDelayMs;
        _body = Encoding.ASCII.GetBytes(new string('x', sizeBytes));
        Url = $"http://127.0.0.1:{port}/{fileName}";

        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();

        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch { return; }

                var response = context.Response;
                response.ContentType = "application/octet-stream";

                if (context.Request.HttpMethod == "HEAD")
                {
                    response.ContentLength64 = _body.Length;
                    response.Close();
                    continue;
                }

                // Answer ranges so the transfer is classified as resumable, matching a real CDN.
                var range = context.Request.Headers["Range"];
                if (range is not null && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
                {
                    var spec = range["bytes=".Length..].Split('-');
                    var from = int.Parse(spec[0]);
                    var to = spec.Length > 1 && spec[1].Length > 0 ? int.Parse(spec[1]) : _body.Length - 1;
                    var slice = _body[from..(to + 1)];

                    response.StatusCode = 206;
                    response.Headers.Add("Content-Range", $"bytes {from}-{to}/{_body.Length}");
                    response.ContentLength64 = slice.Length;
                    await response.OutputStream.WriteAsync(slice);
                    response.Close();
                    continue;
                }

                response.ContentLength64 = _body.Length;
                if (_chunkDelayMs > 0)
                {
                    // Dribble the body out so the transfer takes real time and two downloads
                    // can be observed genuinely overlapping rather than merely both succeeding.
                    const int chunk = 64 * 1024;
                    for (var offset = 0; offset < _body.Length; offset += chunk)
                    {
                        var size = Math.Min(chunk, _body.Length - offset);
                        await response.OutputStream.WriteAsync(_body.AsMemory(offset, size));
                        await response.OutputStream.FlushAsync();
                        await Task.Delay(_chunkDelayMs);
                    }
                }
                else
                {
                    await response.OutputStream.WriteAsync(_body);
                }
                response.Close();
            }
        });
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        _listener.Close();
    }
}

public class HostConnectionTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "hc-tests-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly HostConnection _host = new();

    public HostConnectionTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        _host.Dispose();
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }

    [Fact]
    public void HostLocator_FindsTheBuiltHost()
    {
        var path = HostLocator.TryResolve();
        Assert.True(path is not null, "DownloaderHost.exe was not found; build the host in Release first.");
        Assert.EndsWith("DownloaderHost.exe", path);
    }

    [Fact]
    public async Task Ping_RoundTripsOverOneConnection()
    {
        var reply = await _host.SendAsync("ping");
        Assert.Equal("pong", reply.Status);
        Assert.True(_host.IsConnected);
    }

    /// <summary>
    /// The point of phase 1. The old design started a process per download and closed stdin
    /// immediately, so a second transfer could not overlap the first.
    /// </summary>
    [Fact]
    public async Task TwoDownloads_RunConcurrently_WithIndependentProgress()
    {
        // Throttled so each transfer lasts ~2s; without that both finish too fast for
        // "concurrent" to mean anything.
        using var serverA = new LocalFileServer(8811, 2_000_000, "a.bin", chunkDelayMs: 60);
        using var serverB = new LocalFileServer(8812, 2_000_000, "b.bin", chunkDelayMs: 60);

        var progressA = 0;
        var progressB = 0;
        DateTime firstA = default, lastA = default, firstB = default, lastB = default;
        var pathA = Path.Combine(_folder, "a.bin");
        var pathB = Path.Combine(_folder, "b.bin");

        var taskA = _host.SendAsync("download", new { url = serverA.Url, path = pathA },
            m =>
            {
                if (m.Status != "progress") return;
                if (Interlocked.Increment(ref progressA) == 1) firstA = DateTime.UtcNow;
                lastA = DateTime.UtcNow;
            });
        var taskB = _host.SendAsync("download", new { url = serverB.Url, path = pathB },
            m =>
            {
                if (m.Status != "progress") return;
                if (Interlocked.Increment(ref progressB) == 1) firstB = DateTime.UtcNow;
                lastB = DateTime.UtcNow;
            });

        var results = await Task.WhenAll(taskA, taskB);

        Assert.All(results, r => Assert.Equal("finished", r.Status));
        Assert.Equal(2_000_000, new FileInfo(pathA).Length);
        Assert.Equal(2_000_000, new FileInfo(pathB).Length);

        // Each transfer reported its own progress.
        Assert.True(progressA > 0, "download A reported no progress");
        Assert.True(progressB > 0, "download B reported no progress");

        // The decisive check: their active windows overlapped, so they really did run at the
        // same time rather than one after the other.
        Assert.True(firstA < lastB && firstB < lastA,
            $"transfers did not overlap: A {firstA:HH:mm:ss.fff}-{lastA:HH:mm:ss.fff}, " +
            $"B {firstB:HH:mm:ss.fff}-{lastB:HH:mm:ss.fff}");

        // One process served both: if the connection were per-download this would not hold.
        Assert.True(_host.IsConnected);
    }

    /// <summary>Replies must be matched by id, not by arrival order.</summary>
    [Fact]
    public async Task Replies_AreMatchedById_NotByOrder()
    {
        using var server = new LocalFileServer(8813, 500_000, "c.bin");

        var slow = _host.SendAsync("download",
            new { url = server.Url, path = Path.Combine(_folder, "c.bin") });
        var fast = _host.SendAsync("ping");

        // The ping is issued second but finishes first; it must not consume the download's reply.
        var pong = await fast;
        Assert.Equal("pong", pong.Status);

        var finished = await slow;
        Assert.Equal("finished", finished.Status);
    }

    /// <summary>
    /// A dead host must surface as an error. Previously the UI would simply wait for ever.
    /// </summary>
    [Fact]
    public async Task HostDeath_FailsPendingRequests_AndRaisesDisconnected()
    {
        using var server = new LocalFileServer(8814, 4_000_000, "big.bin", chunkDelayMs: 80);

        var disconnected = new TaskCompletionSource<string>();
        _host.Disconnected += reason => disconnected.TrySetResult(reason);

        var download = _host.SendAsync("download",
            new { url = server.Url, path = Path.Combine(_folder, "big.bin") });

        // Kill ONLY this connection's host, identified by the pid the handshake returns.
        // Killing by process name would take out a host serving a live browser download --
        // see docs/INVARIANTS.md, which exists because that once destroyed 1.47 GB.
        var pong = await _host.SendAsync("ping");
        var pid = pong.Raw.GetProperty("pid").GetInt32();
        System.Diagnostics.Process.GetProcessById(pid).Kill();

        await Assert.ThrowsAnyAsync<Exception>(() => download);
        var completed = await Task.WhenAny(disconnected.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(disconnected.Task, completed);
    }
}
