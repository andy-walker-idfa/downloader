using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DownloaderAppWpf;

/// <summary>Progress reported while the native host streams a file.</summary>
public readonly record struct DownloadStatus(string Status, string? Tier, long Received, long? Total, string? Path, string? Message);

/// <summary>
/// Drives the native host over the same length-prefixed stdio protocol the browser extension
/// uses. See docs/PROTOCOL.md.
/// </summary>
public sealed class DownloaderService
{
    /// <summary>
    /// Locates DownloaderHost.exe. In a packaged (MSIX) build the host ships beside the app;
    /// the dev-tree fallbacks exist so the app also runs from a plain `dotnet build` checkout.
    /// </summary>
    public static string ResolveHostPath()
    {
        var baseDir = AppContext.BaseDirectory;

        var candidates = new List<string>
        {
            // Packaged / published layout: the host sits next to the app.
            Path.Combine(baseDir, "DownloaderHost.exe"),
            Path.Combine(baseDir, "host", "DownloaderHost.exe")
        };

        // Dev tree: app/DownloaderAppWpf/bin/<cfg>/net8.0-windows -> repo root is four levels up.
        var repoRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", ".."));
        foreach (var cfg in new[] { "Release", "Debug" })
        {
            candidates.Add(Path.Combine(repoRoot, "native-host", "DownloaderHost", "bin", cfg, "net8.0", "DownloaderHost.exe"));
        }

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException(
            "DownloaderHost.exe not found. Build it with: dotnet publish native-host/DownloaderHost -c Release");
    }

    public async Task StartDownloadAsync(
        string url,
        string outputFolder,
        IProgress<DownloadStatus>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("URL is required", nameof(url));
        }

        Directory.CreateDirectory(outputFolder);

        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveHostPath(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the downloader host.");

        var stdin = process.StandardInput.BaseStream;
        var stdout = process.StandardOutput.BaseStream;

        await WriteMessageAsync(stdin, new
        {
            cmd = "download",
            id = "app-1",
            url,
            path = Path.Combine(outputFolder, GetFileNameFromUrl(url))
        }, cancellationToken);

        // Closing stdin is what lets the host exit once the transfer ends. Leaving it open made
        // the host block on its read loop forever, so the previous version never returned.
        stdin.Close();

        string? failure = null;
        while (true)
        {
            var message = await ReadMessageAsync(stdout, cancellationToken);
            if (message is null) break;

            var status = message.Value.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
            progress?.Report(new DownloadStatus(
                status,
                message.Value.TryGetProperty("tier", out var t) ? t.GetString() : null,
                message.Value.TryGetProperty("received", out var r) && r.TryGetInt64(out var rv) ? rv : 0,
                message.Value.TryGetProperty("total", out var tot) && tot.TryGetInt64(out var tv) ? tv : null,
                message.Value.TryGetProperty("path", out var p) ? p.GetString() : null,
                message.Value.TryGetProperty("message", out var m) ? m.GetString() : null));

            if (status == "error")
            {
                failure = message.Value.TryGetProperty("message", out var em) ? em.GetString() : "download failed";
                break;
            }

            if (status is "finished" or "cancelled" or "paused") break;
        }

        await process.WaitForExitAsync(cancellationToken);

        if (failure is not null)
        {
            throw new InvalidOperationException(failure);
        }
    }

    private static async Task WriteMessageAsync(Stream stream, object payload, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        await stream.WriteAsync(BitConverter.GetBytes(bytes.Length).AsMemory(0, 4), cancellationToken);
        await stream.WriteAsync(bytes.AsMemory(0, bytes.Length), cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<JsonElement?> ReadMessageAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        if (!await ReadExactlyAsync(stream, header, cancellationToken)) return null;

        var length = BitConverter.ToInt32(header, 0);
        if (length <= 0) return null;

        var payload = new byte[length];
        if (!await ReadExactlyAsync(stream, payload, cancellationToken)) return null;

        using var document = JsonDocument.Parse(payload);
        return document.RootElement.Clone();
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }

    private static string GetFileNameFromUrl(string url)
    {
        try
        {
            var fileName = Path.GetFileName(new Uri(url).LocalPath);
            return string.IsNullOrWhiteSpace(fileName) ? "download.bin" : fileName;
        }
        catch
        {
            return "download.bin";
        }
    }
}
