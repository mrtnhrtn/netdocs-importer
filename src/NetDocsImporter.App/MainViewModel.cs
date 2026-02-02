using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Forms;
using NetDocsImporter.Core;

namespace NetDocsImporter.App;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private const long LargeFileThresholdBytes = 1_800_000_000;

    private string? _selectedFolder;
    private long _totalFiles;
    private long _totalBytes;
    private bool _isScanning;
    private string _statusText = "Ready.";
    private CancellationTokenSource? _cancellation;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<LargeFileView> LargeFiles { get; } = new();

    public string? SelectedFolder
    {
        get => _selectedFolder;
        private set => SetField(ref _selectedFolder, value);
    }

    public string TotalFilesDisplay => _totalFiles.ToString("N0", CultureInfo.CurrentCulture);

    public string TotalBytesDisplay => FormatBytes(_totalBytes);

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (SetField(ref _isScanning, value))
            {
                OnPropertyChanged(nameof(CanStartScan));
            }
        }
    }

    public bool CanStartScan => !IsScanning;

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public async Task SelectFolderAndScanAsync()
    {
        if (IsScanning)
        {
            return;
        }

        using var dialog = new FolderBrowserDialog
        {
            Description = "Select a folder to scan",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };

        if (dialog.ShowDialog() != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            return;
        }

        SelectedFolder = dialog.SelectedPath;
        await StartScanAsync(dialog.SelectedPath);
    }

    public void CancelScan()
    {
        _cancellation?.Cancel();
    }

    private async Task StartScanAsync(string path)
    {
        LargeFiles.Clear();
        _totalFiles = 0;
        _totalBytes = 0;
        OnPropertyChanged(nameof(TotalFilesDisplay));
        OnPropertyChanged(nameof(TotalBytesDisplay));

        StatusText = "Scanning...";
        IsScanning = true;

        _cancellation = new CancellationTokenSource();
        var progress = new Progress<FileScanProgress>(UpdateProgress);

        try
        {
            await FileScanner.ScanAsync(path, LargeFileThresholdBytes, progress, _cancellation.Token);
            StatusText = "Scan complete.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Scan canceled.";
        }
        catch (Exception ex)
        {
            StatusText = $"Scan failed: {ex.Message}";
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            IsScanning = false;
        }
    }

    private void UpdateProgress(FileScanProgress progress)
    {
        _totalFiles = progress.TotalFiles;
        _totalBytes = progress.TotalBytes;
        OnPropertyChanged(nameof(TotalFilesDisplay));
        OnPropertyChanged(nameof(TotalBytesDisplay));

        if (progress.LargeFile is not null)
        {
            LargeFiles.Add(new LargeFileView(progress.LargeFile.Path, progress.LargeFile.Bytes));
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        var size = (double)bytes;
        var order = 0;

        while (size >= 1024 && order < suffixes.Length - 1)
        {
            order++;
            size /= 1024;
        }

        return string.Format(CultureInfo.CurrentCulture, "{0:0.##} {1}", size, suffixes[order]);
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class LargeFileView
{
    public LargeFileView(string path, long bytes)
    {
        Path = path;
        SizeDisplay = FormatBytes(bytes);
    }

    public string Path { get; }

    public string SizeDisplay { get; }

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        var size = (double)bytes;
        var order = 0;

        while (size >= 1024 && order < suffixes.Length - 1)
        {
            order++;
            size /= 1024;
        }

        return string.Format(CultureInfo.CurrentCulture, "{0:0.##} {1}", size, suffixes[order]);
    }
}
