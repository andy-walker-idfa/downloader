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

    private static string LocalAppData => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsDownloader");

    public static string ManifestPath => Path.Combine(LocalAppData, $"{HostName}.json");

    /// <summary>
    /// Where the host is registered from. A package installs to a version-stamped folder
    /// (Name_1.0.0.0_arch__hash), so every update moves the host and deletes the old folder --
    /// leaving the registered manifest pointing at nothing until the app next runs. Registering
    /// a copy at this stable path means an update can never invalidate the registration.
    /// </summary>
    public static string StableHostDir => Path.Combine(LocalAppData, "host");

    private static string StableHostExe => Path.Combine(StableHostDir, "DownloaderHost.exe");

    /// <summary>Identifies the exact build a copy came from, so a stale copy is detected.</summary>
    private static string StampFor(FileInfo exe) =>
        $"{exe.FullName}|{exe.Length}|{exe.LastWriteTimeUtc.Ticks}";

    /// <summary>
    /// Copies the host out of the package to a stable location, if it is not already current.
    /// Returns the path to register, falling back to the source when copying is not possible.
    /// </summary>
    private static string CopyHostToStableLocation(string sourceExe, RegistrationResult result)
    {
        var source = new FileInfo(sourceExe);
        var stampFile = Path.Combine(StableHostDir, ".source");
        var wanted = StampFor(source);

        try
        {
            if (File.Exists(StableHostExe) && File.Exists(stampFile) &&
                File.ReadAllText(stampFile).Trim() == wanted)
            {
                result.HostCopy = HostCopyState.AlreadyCurrent;
                return StableHostExe;
            }

            Directory.CreateDirectory(StableHostDir);

            // Copy the whole directory: a self-contained host is more than one file.
            var sourceDir = source.Directory!;
            foreach (var file in sourceDir.GetFiles("*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceDir.FullName, file.FullName);
                var destination = Path.Combine(StableHostDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                file.CopyTo(destination, overwrite: true);
            }

            File.WriteAllText(stampFile, wanted);
            result.HostCopy = HostCopyState.Copied;
            return StableHostExe;
        }
        catch (IOException ex)
        {
            // Almost always "file in use": a browser has the host open right now. An existing
            // copy still works, so keep registering it rather than failing outright -- a
            // slightly stale host beats a broken registration.
            if (File.Exists(StableHostExe))
            {
                result.HostCopy = HostCopyState.KeptExisting;
                result.Note = $"could not refresh the host copy ({ex.Message}); kept the existing one";
                return StableHostExe;
            }

            result.HostCopy = HostCopyState.Failed;
            result.Note = $"could not copy the host ({ex.Message}); registered it in place";
            return sourceExe;
        }
        catch (UnauthorizedAccessException ex)
        {
            result.HostCopy = HostCopyState.Failed;
            result.Note = $"could not copy the host ({ex.Message}); registered it in place";
            return File.Exists(StableHostExe) ? StableHostExe : sourceExe;
        }
    }

    /// <summary>
    /// Locates the host. In a package it sits beside the app; the dev-tree fallbacks let the
    /// app also run from a plain checkout.
    /// </summary>
    public static string? FindHost()
    {
        return HostLocator.TryResolve();
    }

    /// <summary>
    /// Writes the host manifest and the per-browser registry entries. Idempotent: safe to call
    /// on every launch, which also repairs a registration a browser update or a profile reset
    /// has removed.
    /// </summary>
    public static RegistrationResult EnsureRegistered() => EnsureRegistered(copyHost: IsPackaged());

    /// <param name="copyHost">
    /// Copy the host to a stable path before registering. Always wanted when packaged; the
    /// parameter exists so the behaviour can be exercised from a dev build too.
    /// </param>
    public static RegistrationResult EnsureRegistered(bool copyHost)
    {
        var result = new RegistrationResult { Packaged = IsPackaged() };

        var hostExe = FindHost();
        if (hostExe is null)
        {
            result.Error = "DownloaderHost.exe not found next to the app or in the dev tree.";
            return result;
        }

        result.SourceHostPath = hostExe;
        if (copyHost)
        {
            hostExe = CopyHostToStableLocation(hostExe, result);
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

    public enum HostCopyState { NotAttempted, Copied, AlreadyCurrent, KeptExisting, Failed }

    public sealed class RegistrationResult
    {
        public bool Packaged { get; set; }
        public string? SourceHostPath { get; set; }
        public string? HostPath { get; set; }
        public string? ManifestPath { get; set; }
        public string? Error { get; set; }
        public string? Note { get; set; }
        public HostCopyState HostCopy { get; set; } = HostCopyState.NotAttempted;
        public List<string> Registered { get; } = new();
        public List<string> Failed { get; } = new();

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"packaged     : {Packaged}");
            sb.AppendLine($"host source  : {SourceHostPath ?? "(not found)"}");
            sb.AppendLine($"host copy    : {HostCopy}");
            sb.AppendLine($"registered as: {HostPath ?? "(none)"}");
            sb.AppendLine($"manifest     : {ManifestPath ?? "(not written)"}");
            sb.AppendLine($"registered   : {(Registered.Count > 0 ? string.Join(", ", Registered) : "none")}");
            if (Failed.Count > 0) sb.AppendLine($"failed       : {string.Join("; ", Failed)}");
            if (Note is not null) sb.AppendLine($"note         : {Note}");
            if (Error is not null) sb.AppendLine($"error        : {Error}");
            return sb.ToString();
        }
    }
}
