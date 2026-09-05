using System;
using System.Collections.Generic;
using System.IO;

namespace DownloaderAppWpf;

/// <summary>
/// Finds DownloaderHost.exe. Shared by the registrar and the connection so there is one answer
/// to "where is the host", not two that can disagree.
/// </summary>
public static class HostLocator
{
    /// <summary>
    /// In a packaged build the host ships in a host\ subfolder beside the app. The dev-tree
    /// fallbacks let the app run from a plain checkout as well.
    /// </summary>
    public static string Resolve()
    {
        var baseDir = AppContext.BaseDirectory;

        var candidates = new List<string>
        {
            Path.Combine(baseDir, "host", "DownloaderHost.exe"),
            Path.Combine(baseDir, "DownloaderHost.exe")
        };

        // app\DownloaderAppWpf\bin\<cfg>\net8.0-windows -> repo root is five levels up.
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

    /// <summary>Resolve without throwing; null when the host is not present.</summary>
    public static string? TryResolve()
    {
        try { return Resolve(); }
        catch (FileNotFoundException) { return null; }
    }
}
