using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using Microsoft.Win32;

namespace DownloaderAppWpf;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<DownloadItem> _downloads = new();

    public MainWindow()
    {
        InitializeComponent();
        Title += "  -  " + DescribeRegistration();
        DownloadsGrid.ItemsSource = _downloads;

        // A dead host must be visible, not a UI that waits for ever.
        App.Host.Disconnected += reason => OnUi(() =>
        {
            foreach (var item in _downloads.Where(d => d.IsActive))
            {
                item.Status = "Error";
                item.TierText = "host disconnected: " + reason;
            }

            UpdateStatus();
        });

        UpdateStatus();
    }

    private static string DescribeRegistration()
    {
        var r = App.Registration;
        if (r is null) return "native host: registration not attempted";
        if (r.Error is not null) return $"native host: FAILED - {r.Error}";
        var where = r.Packaged ? "packaged" : "unpackaged";
        return $"native host: registered for {string.Join(", ", r.Registered)} ({where})";
    }

    /// <summary>Host replies arrive on a background thread; UI state changes must not.</summary>
    private void OnUi(Action action) => Dispatcher.Invoke(action);

    private void AddDownload_Click(object sender, RoutedEventArgs e)
    {
        var url = UrlTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(url)) return;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            MessageBox.Show("Enter a http:// or https:// address.", "Not a download link",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var item = new DownloadItem { Name = GuessName(url), Status = "Starting", Url = url };
        _downloads.Insert(0, item);
        UrlTextBox.Clear();
        UpdateStatus();

        // Deliberately not awaited: awaiting here is what limited the app to one download at a
        // time. Each transfer owns its own task and reports through the item.
        _ = RunDownloadAsync(item, url);
    }

    private async System.Threading.Tasks.Task RunDownloadAsync(DownloadItem item, string url)
    {
        try
        {
            // StartDownload hands back the id immediately, which is what makes it possible to
            // pause or cancel this specific transfer while it runs.
            var handle = App.Host.StartDownload(
                new { url },
                interim => OnUi(() => ApplyInterim(item, interim)));

            item.Handle = handle;
            var result = await handle.Completion;

            OnUi(() =>
            {
                switch (result.Status)
                {
                    case "finished":
                        item.Status = "Complete";
                        item.ProgressText = "100%";
                        item.OffsetText = DownloadItem.FormatBytes(result.Bytes);
                        if (result.Path is not null) item.Name = Path.GetFileName(result.Path);
                        break;
                    case "paused":
                        item.Status = "Paused";
                        break;
                    case "cancelled":
                        item.Status = "Cancelled";
                        break;
                    case "error":
                        item.Status = "Error";
                        item.TierText = result.Message ?? "failed";
                        break;
                    default:
                        item.Status = result.Status;
                        break;
                }

                item.IsStopping = false;
                item.Handle = null;
                UpdateStatus();
            });
        }
        catch (Exception ex)
        {
            OnUi(() =>
            {
                item.Status = "Error";
                item.TierText = ex.Message;
                item.IsStopping = false;
                item.Handle = null;
                UpdateStatus();
            });
        }
    }

    private static void ApplyInterim(DownloadItem item, HostMessage message)
    {
        switch (message.Status)
        {
            case "started":
                item.Status = "Downloading";
                item.TierText = message.Tier ?? "";
                item.Resumable = message.Resumable;
                if (message.Path is not null) item.Name = Path.GetFileName(message.Path);
                if (message.Total is > 0) item.SizeText = DownloadItem.FormatBytes(message.Total.Value);
                break;

            case "progress":
                item.OffsetText = DownloadItem.FormatBytes(message.Received);
                if (message.Total is > 0)
                {
                    item.SizeText = DownloadItem.FormatBytes(message.Total.Value);
                    item.ProgressText = $"{message.Received * 100.0 / message.Total.Value:F0}%";
                }
                break;
        }
    }

    private async void PauseRow_Click(object sender, RoutedEventArgs e) =>
        await StopAsync(sender, pause: true);

    private async void CancelRow_Click(object sender, RoutedEventArgs e) =>
        await StopAsync(sender, pause: false);

    /// <summary>
    /// Pause keeps the partial file so the transfer can be resumed; cancel discards it. The host
    /// decides which happened and reports it on the original request, so nothing is assumed here.
    /// </summary>
    private async System.Threading.Tasks.Task StopAsync(object sender, bool pause)
    {
        if ((sender as FrameworkElement)?.DataContext is not DownloadItem item) return;
        if (item.Handle is not { } handle) return;

        item.IsStopping = true;
        item.Status = pause ? "Pausing" : "Cancelling";

        try
        {
            if (pause) await handle.PauseAsync();
            else await handle.CancelAsync();
        }
        catch (Exception ex)
        {
            item.IsStopping = false;
            item.TierText = ex.Message;
        }
    }

    private void UpdateStatus()
    {
        var active = _downloads.Count(d => d.IsActive);
        var done = _downloads.Count(d => d.Status == "Complete");
        var failed = _downloads.Count(d => d.Status == "Error");

        var parts = new System.Collections.Generic.List<string>();
        if (active > 0) parts.Add($"{active} downloading");
        if (done > 0) parts.Add($"{done} complete");
        if (failed > 0) parts.Add($"{failed} failed");

        StatusText.Text = parts.Count == 0 ? "Ready" : string.Join("  -  ", parts);
    }

    private async void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        // A real folder picker. The previous version used a file dialog with a placeholder
        // filename, then only remembered the choice until the app closed.
        var dialog = new OpenFolderDialog { Title = "Choose where downloads are saved" };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var reply = await App.Host.SendAsync("set_settings", new { downloadDir = dialog.FolderName });
            if (reply.Status == "error")
            {
                MessageBox.Show(reply.Message ?? "The folder could not be used.", "Download folder",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            StatusText.Text = "Saving downloads to " + dialog.FolderName;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Download folder", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string GuessName(string url)
    {
        // Only a placeholder until the host reports the real name it resolved.
        try
        {
            var name = Path.GetFileName(new Uri(url).LocalPath);
            return string.IsNullOrWhiteSpace(name) ? "(resolving...)" : name;
        }
        catch
        {
            return "(resolving...)";
        }
    }
}

/// <summary>
/// A row in the downloads grid. Implements INotifyPropertyChanged because WPF bindings are
/// one-shot against a plain object: without it the grid showed "Queued" for the whole transfer.
/// </summary>
public class DownloadItem : INotifyPropertyChanged
{
    private string _name = "download.bin";
    private string _status = "Queued";
    private string _progressText = "0%";
    private string _offsetText = "0 B";
    private string _sizeText = "-";
    private string _tierText = "";
    private bool _resumable;
    private bool _isStopping;

    public string Url { get; init; } = "";

    /// <summary>The running transfer, so it can be named when pausing or cancelling.</summary>
    public DownloadHandle? Handle { get; set; }

    public string Name { get => _name; set => Set(ref _name, value); }
    public string ProgressText { get => _progressText; set => Set(ref _progressText, value); }
    public string OffsetText { get => _offsetText; set => Set(ref _offsetText, value); }
    public string SizeText { get => _sizeText; set => Set(ref _sizeText, value); }
    public string TierText { get => _tierText; set => Set(ref _tierText, value); }

    public string Status
    {
        get => _status;
        set { if (Set(ref _status, value)) NotifyButtons(); }
    }

    /// <summary>Whether the server honours ranges. Decides whether Pause is offered at all.</summary>
    public bool Resumable
    {
        get => _resumable;
        set { if (Set(ref _resumable, value)) NotifyButtons(); }
    }

    /// <summary>A pause or cancel is in flight; the buttons stay disabled until it lands.</summary>
    public bool IsStopping
    {
        get => _isStopping;
        set { if (Set(ref _isStopping, value)) NotifyButtons(); }
    }

    public bool IsActive => Status is "Starting" or "Downloading" or "Pausing" or "Cancelling";

    /// <summary>
    /// Pause is offered only where the server can actually resume. On a non-resumable source it
    /// would silently mean "discard everything downloaded so far", so Cancel is the honest word.
    /// </summary>
    public bool CanPause => IsActive && Resumable && !IsStopping;

    public bool CanCancel => IsActive && !IsStopping;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void NotifyButtons()
    {
        Notify(nameof(IsActive));
        Notify(nameof(CanPause));
        Notify(nameof(CanCancel));
    }

    private void Notify(string property) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Notify(property!);
        return true;
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024:F1} MB";
        return $"{bytes / 1024.0 / 1024 / 1024:F2} GB";
    }
}
