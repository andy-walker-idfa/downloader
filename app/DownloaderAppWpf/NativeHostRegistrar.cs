using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace DownloaderAppWpf;

/// <summary>
/// Registers the native messaging host with the installed Chromium browsers.
///
/// This runs at app startup rather than at install time, and that is the whole point. An MSIX
/// package can declare registry keys, but they land in the package's virtualised hive, which
/// browsers running outside the package cannot see. A full-trust packaged app writing HKCU at
/// runtime is the mechanism that actually works.
/// </summary>
public static class NativeHostRegistrar
{
    public const string HostName = "com.downloader.host";

    /// <summary>
    /// The extension's ID, fixed by the signing key in extension/manifest.json. It no longer
    /// depends on the install location, which is what makes registering it from a package
    /// possible at all.
    /// </summary>
    public const string ExtensionId = "febdocdjpdhmfddcddbobidgpjhckemo";

    private static readonly (string Browser, string Key)[] Targets =
    {
        ("Chrome", @"Software\Google\Chrome\NativeMessagingHosts\"),
        // Brave's key is 'Brave-Browser', not 'Brave'.
        ("Brave",  @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\"),
        ("Edge",   @"Software\Microsoft\Edge\NativeMessagingHosts\")
    };

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, StringBuilder? packageFullName);

    private const int AppModelErrorNoPackage = 15700;

    /// <summary>True when running from inside an MSIX package.</summary>
    public static bool IsPackaged()
    {
        try
        {
            var length = 0;
            return GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage;
        }
        catch
        {
            return false; // the API is absent on older Windows; treat as unpackaged
        }
    }

    public static string ManifestPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WindowsDownloader", $"{HostName}.json");

    /// <summary>
    /// Locates the host. In a package it sits beside the app; the dev-tree fallbacks let the
    /// app also run from a plain checkout.
    /// </summary>
    public static string? FindHost()
    {
        try { return DownloaderService.ResolveHostPath(); }
        catch (FileNotFoundException) { return null; }
    }

    /// <summary>
    /// Writes the host manifest and the per-browser registry entries. Idempotent: safe to call
    /// on every launch, which also repairs a registration a browser update or a profile reset
    /// has removed.
    /// </summary>
    public static RegistrationResult EnsureRegistered()
    {
        var result = new RegistrationResult { Packaged = IsPackaged() };

        var hostExe = FindHost();
        if (hostExe is null)
        {
            result.Error = "DownloaderHost.exe not found next to the app or in the dev tree.";
            return result;
        }

        result.HostPath = hostExe;

        var manifest = new
        {
            name = HostName,
            description = "Windows Downloader native host",
            path = hostExe,
            type = "stdio",
            allowed_origins = new[] { $"chrome-extension://{ExtensionId}/" }
        };

        try
        {
            var manifestPath = ManifestPath;
            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            result.ManifestPath = manifestPath;
        }
        catch (Exception ex)
        {
            result.Error = $"Could not write the host manifest: {ex.Message}";
            return result;
        }

        foreach (var (browser, keyPath) in Targets)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(keyPath + HostName, writable: true);
                key.SetValue(string.Empty, result.ManifestPath, RegistryValueKind.String);
                result.Registered.Add(browser);
            }
            catch (Exception ex)
            {
                result.Failed.Add($"{browser}: {ex.Message}");
            }
        }

        return result;
    }

    public sealed class RegistrationResult
    {
        public bool Packaged { get; set; }
        public string? HostPath { get; set; }
        public string? ManifestPath { get; set; }
        public string? Error { get; set; }
        public List<string> Registered { get; } = new();
        public List<string> Failed { get; } = new();

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"packaged     : {Packaged}");
            sb.AppendLine($"host         : {HostPath ?? "(not found)"}");
            sb.AppendLine($"manifest     : {ManifestPath ?? "(not written)"}");
            sb.AppendLine($"registered   : {(Registered.Count > 0 ? string.Join(", ", Registered) : "none")}");
            if (Failed.Count > 0) sb.AppendLine($"failed       : {string.Join("; ", Failed)}");
            if (Error is not null) sb.AppendLine($"error        : {Error}");
            return sb.ToString();
        }
    }
}
