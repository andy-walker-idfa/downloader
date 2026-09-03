using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace DownloaderAppWpf;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<DownloadItem> _downloads = new();
    private readonly DownloaderService _downloaderService = new();

    private string DescribeRegistration()
    {
        var r = App.Registration;
        if (r is null) return "native host: registration not attempted";
        if (r.Error is not null) return $"native host: FAILED - {r.Error}";
        var where = r.Packaged ? "packaged" : "unpackaged";
        return $"native host: registered for {string.Join(", ", r.Registered)} ({where})";
    }
    private string _currentDownloadFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    public MainWindow()
    {
        InitializeComponent();
        Title += "  -  " + DescribeRegistration();
        DownloadsGrid.ItemsSource = _downloads;
    }

    private async void AddDownload_Click(object sender, RoutedEventArgs e)
    {
        var url = UrlTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        var name = GetFileNameFromUrl(url);
        var item = new DownloadItem
        {
            Name = name,
            Status = "Queued",
            ProgressText = "0%",
            OffsetText = "0 B",
            SizeText = "0 MB"
        };

        _downloads.Add(item);
        UrlTextBox.Clear();

        // Progress<T> marshals back to the UI thread, so the row updates as bytes arrive.
        var progress = new Progress<DownloadStatus>(update =>
        {
            switch (update.Status)
            {
                case "started":
                    item.Status = "Downloading";
                    item.OffsetText = update.Tier ?? "";
                    if (update.Total is > 0) item.SizeText = DownloadItem.FormatBytes(update.Total.Value);
                    break;
                case "progress":
                    item.OffsetText = DownloadItem.FormatBytes(update.Received);
                    if (update.Total is > 0)
                    {
                        item.SizeText = DownloadItem.FormatBytes(update.Total.Value);
                        item.ProgressText = $"{update.Received * 100.0 / update.Total.Value:F0}%";
                    }
                    break;
                case "paused":
                    item.Status = "Paused";
                    break;
            }
        });

        try
        {
            item.Status = "Downloading";
            await _downloaderService.StartDownloadAsync(url, _currentDownloadFolder, progress);
            item.Status = "Complete";
            item.ProgressText = "100%";
        }
        catch (Exception ex)
        {
            item.Status = "Error";
            item.ProgressText = ex.Message;
        }
    }

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            CheckFileExists = false,
            ValidateNames = false,
            FileName = "Select a downloads folder"
        };

        if (dialog.ShowDialog() == true)
        {
            var selectedPath = Path.GetDirectoryName(dialog.FileName);
            if (!string.IsNullOrWhiteSpace(selectedPath))
            {
                _currentDownloadFolder = selectedPath;
                MessageBox.Show($"Selected folder: {selectedPath}", "Downloads folder", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (DownloadsGrid.SelectedItem is DownloadItem item)
        {
            item.Status = "Paused";
            item.OffsetText = "saved";
        }
    }

    private void Resume_Click(object sender, RoutedEventArgs e)
    {
        if (DownloadsGrid.SelectedItem is DownloadItem item)
        {
            item.Status = "Resuming";
            item.ProgressText = "25%";
            item.OffsetText = "resume";
        }
    }

    private static string GetFileNameFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            var fileName = Path.GetFileName(uri.LocalPath);
            return string.IsNullOrWhiteSpace(fileName) ? "download.bin" : fileName;
        }
        catch
        {
            return "download.bin";
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
    private string _sizeText = "0 MB";

    public string Name { get => _name; set => Set(ref _name, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string ProgressText { get => _progressText; set => Set(ref _progressText, value); }
    public string OffsetText { get => _offsetText; set => Set(ref _offsetText, value); }
    public string SizeText { get => _sizeText; set => Set(ref _sizeText, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set(ref string field, string value, [CallerMemberName] string? property = null)
    {
        if (field == value) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024:F1} MB";
        return $"{bytes / 1024.0 / 1024 / 1024:F2} GB";
    }
}
