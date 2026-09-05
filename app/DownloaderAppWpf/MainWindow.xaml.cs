using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;

namespace DownloaderAppWpf;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<DownloadItem> _downloads = new();

    /// <summary>
    /// Downloads left unfinished by an earlier session. The host reads these from disk, so they
    /// survive an app restart, a crash, and the browser closing.
    /// </summary>
    private readonly ObservableCollection<PartialItem> _partials = new();

    public MainWindow()
    {
        InitializeComponent();
        DownloadsGrid.ItemsSource = _downloads;
        PartialsGrid.ItemsSource = _partials;

        // A dead host must be visible, not a UI that waits for ever.
        App.Host.Disconnected += reason => OnUi(() =>
        {
            foreach (var item in _downloads.Where(d => d.IsActive))
            {
                item.Status = "Error";
                item.Message = "host disconnected: " + reason;
            }

            UpdateStatus();
        });

        ShowRegistrationProblem();
        UpdateStatus();

        Loaded += async (_, _) =>
        {
            await RefreshSettingsAsync();
            await RefreshPartialsAsync();
        };
    }

    // --- unfinished downloads and settings ------------------------------------

    /// <summary>
    /// Asks the host what can be resumed. It scans the download folder for .part files, so this
    /// is the only source that survives the app being closed. Running transfers are excluded by
    /// the host, so nothing appears twice.
    /// </summary>
    private async System.Threading.Tasks.Task RefreshPartialsAsync()
    {
        try
        {
            var reply = await App.Host.SendAsync("list_partials");
            if (reply.Status != "partials") return;

            var items = new List<PartialItem>();
            if (reply.Raw.TryGetProperty("items", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in array.EnumerateArray())
                {
                    items.Add(PartialItem.FromJson(element));
                }
            }

            OnUi(() =>
            {
                _partials.Clear();
                foreach (var item in items) _partials.Add(item);
                UnfinishedPanel.Visibility = _partials.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            });
        }
        catch (Exception ex)
        {
            OnUi(() => StatusText.Text = "Could not list unfinished downloads: " + ex.Message);
        }
    }

    private async System.Threading.Tasks.Task RefreshSettingsAsync()
    {
        try
        {
            var reply = await App.Host.SendAsync("get_settings");
            if (reply.Status != "settings") return;

            var folder = reply.Raw.TryGetProperty("downloadDir", out var d) ? d.GetString() : null;
            if (folder is not null) OnUi(() => FolderText.Text = "Saving to " + folder);
        }
        catch
        {
            // Not fatal: the host reports where each file landed anyway.
        }
    }

    private async void ResumeRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PartialItem partial) return;

        _partials.Remove(partial);
        UnfinishedPanel.Visibility = _partials.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        var item = new DownloadItem { Name = partial.FileName, Status = "Starting", Url = partial.Url };
        _downloads.Insert(0, item);
        UpdateStatus();

        // The same path is what makes this a resume rather than a fresh download: the host reads
        // the existing .part and continues from its length.
        await RunDownloadAsync(item, new { url = partial.Url, path = partial.Path });
    }

    private async void DiscardRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PartialItem partial) return;

        var confirm = MessageBox.Show(
            $"Delete the partly downloaded {partial.FileName} ({partial.DownloadedText})?",
            "Discard download", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        try
        {
            await App.Host.SendAsync("discard", new { path = partial.Path });
            OnUi(() =>
            {
                _partials.Remove(partial);
                UnfinishedPanel.Visibility = _partials.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Discard download", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Registering the native host is what lets the browser extension work at all, so a failure
    /// has to be visible. It used to be appended to the window title, where nobody would read it.
    /// </summary>
    private void ShowRegistrationProblem()
    {
        var r = App.Registration;

        string? problem = r switch
        {
            null => "The native host was not registered, so the browser extension cannot reach it.",
            { Error: not null } => "The native host could not be registered: " + r.Error,
            { Registered.Count: 0 } => "No browser was registered, so the extension cannot reach the downloader.",
            _ => null
        };

        if (problem is null)
        {
            RegistrationBanner.Visibility = Visibility.Collapsed;
            return;
        }

        RegistrationBannerText.Text = problem;
        RegistrationBanner.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Closing the window disposes the connection, which closes the host's stdin and ends every
    /// transfer it is running. Say so rather than losing a download silently -- on a
    /// non-resumable source those bytes cannot be recovered.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        var active = _downloads.Count(d => d.IsActive);
        if (active > 0)
        {
            var answer = MessageBox.Show(
                active == 1
                    ? "A download is still running and will stop if you close." + "\n\n" + "Close anyway?"
                    : $"{active} downloads are still running and will stop if you close." + "\n\n" + "Close anyway?",
                "Downloads in progress", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

            if (answer != MessageBoxResult.OK)
            {
                e.Cancel = true;
                return;
            }
        }

        base.OnClosing(e);
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
        _ = RunDownloadAsync(item, new { url });
    }

    private async System.Threading.Tasks.Task RunDownloadAsync(DownloadItem item, object arguments)
    {
        try
        {
            // StartDownload hands back the id immediately, which is what makes it possible to
            // pause or cancel this specific transfer while it runs.
            var handle = App.Host.StartDownload(
                arguments,
                interim => OnUi(() =>
                {
                    ApplyInterim(item, interim);
                    UpdateStatus();
                }));

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
                        item.Message = result.Message ?? "failed";
                        break;
                    default:
                        item.Status = result.Status;
                        break;
                }

                item.IsStopping = false;
                item.Handle = null;
                UpdateStatus();
            });

            await RefreshPartialsAsync();
        }
        catch (Exception ex)
        {
            OnUi(() =>
            {
                item.Status = "Error";
                item.Message = ex.Message;
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
            item.Message = ex.Message;
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
        EmptyHint.Visibility = _downloads.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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

            FolderText.Text = "Saving to " + dialog.FolderName;

            // list_partials scans the download folder, so changing it changes what can be resumed.
            await RefreshPartialsAsync();
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
/// A download left unfinished on disk, as reported by the host's list_partials.
/// </summary>
public class PartialItem
{
    public string FileName { get; init; } = "";
    public string Path { get; init; } = "";
    public string Url { get; init; } = "";
    public long BytesOnDisk { get; init; }
    public long? ContentLength { get; init; }
    public string Tier { get; init; } = "";
    public bool Resumable { get; init; }

    public string DownloadedText => DownloadItem.FormatBytes(BytesOnDisk);

    public string SizeText => ContentLength is > 0
        ? DownloadItem.FormatBytes(ContentLength.Value)
        : "unknown";

    /// <summary>
    /// Named honestly: a non-resumable source cannot continue, so pressing this starts again
    /// from zero. Calling it "Resume" there would be a lie.
    /// </summary>
    public string ActionText => Resumable ? "Resume" : "Restart";

    public string ActionHint => Resumable
        ? "Continues from " + DownloadedText
        : Tier + ": this server cannot resume, so it starts over";

    public static PartialItem FromJson(JsonElement element)
    {
        string? Text(string name) =>
            element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        long? Number(string name) =>
            element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)
                ? n
                : null;

        return new PartialItem
        {
            FileName = Text("fileName") ?? "(unknown)",
            Path = Text("path") ?? "",
            Url = Text("url") ?? "",
            Tier = Text("tier") ?? "",
            BytesOnDisk = Number("bytesOnDisk") ?? 0,
            ContentLength = Number("contentLength"),
            Resumable = element.TryGetProperty("resumable", out var r) && r.ValueKind == JsonValueKind.True
        };
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
    private string _message = "";
    private bool _resumable;
    private bool _isStopping;

    public string Url { get; init; } = "";

    /// <summary>The running transfer, so it can be named when pausing or cancelling.</summary>
    public DownloadHandle? Handle { get; set; }

    public string Name { get => _name; set => Set(ref _name, value); }
    public string ProgressText { get => _progressText; set => Set(ref _progressText, value); }
    public string OffsetText { get => _offsetText; set => Set(ref _offsetText, value); }
    public string SizeText { get => _sizeText; set => Set(ref _sizeText, value); }
    public string TierText
    {
        get => _tierText;
        set { if (Set(ref _tierText, value)) Notify(nameof(TierHint)); }
    }

    /// <summary>
    /// Errors and notes. Kept separate from TierText, which previously doubled as the error
    /// field, so a failure was displayed to the user under the heading "Tier".
    /// </summary>
    public string Message { get => _message; set => Set(ref _message, value); }

    /// <summary>The same explanation the browser extension gives for each tier.</summary>
    public string TierHint => TierText switch
    {
        "FullyResumable" => "Range requests honoured with a strong ETag. Safe to resume.",
        "ResumableUnverified" => "Range requests honoured but no strong validator. Resume works, but the server could swap the file underneath.",
        "NotResumable" => "Server ignores Range requests. An interrupted download must restart from zero.",
        "UnboundedStream" => "No Content-Length. The size is unknown and resume is impossible.",
        _ => ""
    };

    public bool HasFailed => Status == "Error";

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
        Notify(nameof(HasFailed));
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
