using System;
using System.IO;
using System.Windows;

namespace DownloaderAppWpf;

public partial class App : Application
{
    /// <summary>Result of the startup registration, surfaced in the window for diagnosis.</summary>
    public static NativeHostRegistrar.RegistrationResult? Registration { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Register the native messaging host on every launch. It is idempotent, and doing it at
        // runtime -- rather than declaring it in the package -- is what makes the registration
        // visible to browsers running outside the MSIX container.
        try
        {
            Registration = NativeHostRegistrar.EnsureRegistered();
            Log(Registration.ToString());
        }
        catch (Exception ex)
        {
            // Never let a registration failure stop the app from opening; the window reports it.
            Log($"registration threw: {ex}");
        }
    }

    private static void Log(string text)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WindowsDownloader");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "app.log"),
                $"[{DateTimeOffset.Now:O}] startup{Environment.NewLine}{text}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never break startup.
        }
    }
}
