using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Forms;
using NetDocsImporter.Core;
using NetDocsImporter.Data;

namespace NetDocsImporter.App;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private const long LargeFileThresholdBytes = 1_800_000_000;

    private string? _selectedFolder;
    private long _totalFiles;
    private long _totalBytes;
    private bool _isScanning;
    private string _statusText = "Ready.";
    private string? _currentJobId;
    private JobSummaryView? _selectedRecentJob;
    private int _maxConcurrency = 4;
    private int _delayBetweenStarts = 250;
    private bool _isImportRunning;
    private bool _isImportPaused;
    private long _importTotalFiles;
    private long _importQueued;
    private long _importRunning;
    private long _importSucceeded;
    private long _importFailed;
    private long _importCanceled;
    private string _selectedFolderPath = "Select a folder.";
    private long _selectedFolderFiles;
    private long _selectedFolderBytes;
    private long _selectedFolderLargeFiles;
    private long _selectedFolderExcludedFolders;
    private long _includedFilesCount;
    private long _excludedFilesCount;
    private CancellationTokenSource? _cancellation;
    private CancellationTokenSource? _importCancellation;
    private readonly AppPaths _paths;
    private readonly JobStore _jobStore;
    private readonly ScanJobRunner _jobRunner;
    private readonly SynchronizationContext? _uiContext;
    private ImportPipeline? _importPipeline;
    private readonly Random _importRandom = new();
    private readonly object _importRefreshLock = new();
    private bool _importRefreshPending;
    private readonly IFolderTreeProvider _folderProvider;
    private FolderNodeViewModel? _selectedFolderNode;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<LargeFileView> LargeFiles { get; } = new();
    public ObservableCollection<JobSummaryView> RecentJobs { get; } = new();
    public ObservableCollection<TransferView> LatestTransfers { get; } = new();
    public ObservableCollection<TreeNodeBase> FolderRoots { get; } = new();

    public string? SelectedFolder
    {
        get => _selectedFolder;
        private set => SetField(ref _selectedFolder, value);
    }

    public string TotalFilesDisplay => _totalFiles.ToString("N0", CultureInfo.CurrentCulture);

    public string TotalBytesDisplay => FormatBytes(_totalBytes);

    public string? CurrentJobId
    {
        get => _currentJobId;
        private set
        {
            if (SetField(ref _currentJobId, value))
            {
                OnPropertyChanged(nameof(CanStartImport));
            }
        }
    }

    public JobSummaryView? SelectedRecentJob
    {
        get => _selectedRecentJob;
        set
        {
            if (SetField(ref _selectedRecentJob, value) && value is not null)
            {
                CurrentJobId = value.JobId;
                _ = LoadFolderTreeAsync();
                _ = RefreshImportDataAsync();
            }
        }
    }

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

    public int MaxConcurrency
    {
        get => _maxConcurrency;
        set => SetField(ref _maxConcurrency, value);
    }

    public int DelayMsBetweenStarts
    {
        get => _delayBetweenStarts;
        set => SetField(ref _delayBetweenStarts, value);
    }

    public bool IsImportRunning
    {
        get => _isImportRunning;
        private set
        {
            if (SetField(ref _isImportRunning, value))
            {
                OnPropertyChanged(nameof(CanStartImport));
                OnPropertyChanged(nameof(CanPauseImport));
                OnPropertyChanged(nameof(CanResumeImport));
                OnPropertyChanged(nameof(CanCancelImport));
            }
        }
    }

    public bool IsImportPaused
    {
        get => _isImportPaused;
        private set
        {
            if (SetField(ref _isImportPaused, value))
            {
                OnPropertyChanged(nameof(CanPauseImport));
                OnPropertyChanged(nameof(CanResumeImport));
            }
        }
    }

    public bool CanStartImport => !IsImportRunning && !string.IsNullOrWhiteSpace(CurrentJobId);

    public bool CanPauseImport => IsImportRunning && !IsImportPaused;

    public bool CanResumeImport => IsImportRunning && IsImportPaused;

    public bool CanCancelImport => IsImportRunning;

    public string ImportTotalFilesDisplay => _importTotalFiles.ToString("N0", CultureInfo.CurrentCulture);

    public string ImportQueuedDisplay => _importQueued.ToString("N0", CultureInfo.CurrentCulture);

    public string ImportRunningDisplay => _importRunning.ToString("N0", CultureInfo.CurrentCulture);

    public string ImportSucceededDisplay => _importSucceeded.ToString("N0", CultureInfo.CurrentCulture);

    public string ImportFailedDisplay => _importFailed.ToString("N0", CultureInfo.CurrentCulture);

    public string ImportCanceledDisplay => _importCanceled.ToString("N0", CultureInfo.CurrentCulture);

    public string SelectedFolderPath => _selectedFolderPath;

    public string SelectedFolderFilesDisplay => _selectedFolderFiles.ToString("N0", CultureInfo.CurrentCulture);

    public string SelectedFolderBytesDisplay => FormatBytes(_selectedFolderBytes);

    public string SelectedFolderLargeFilesDisplay => _selectedFolderLargeFiles.ToString("N0", CultureInfo.CurrentCulture);

    public string SelectedFolderExcludedDisplay => _selectedFolderExcludedFolders.ToString("N0", CultureInfo.CurrentCulture);

    public string IncludedFilesCountDisplay => _includedFilesCount.ToString("N0", CultureInfo.CurrentCulture);

    public string ExcludedFilesCountDisplay => _excludedFilesCount.ToString("N0", CultureInfo.CurrentCulture);

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public MainViewModel()
    {
        _paths = new AppPaths();
        _jobStore = new JobStore(_paths.DatabasePath);
        _jobRunner = new ScanJobRunner(_jobStore);
        _uiContext = SynchronizationContext.Current;
        _folderProvider = new JobStoreFolderTreeProvider(_jobStore);
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

        var jobId = Guid.NewGuid().ToString("N");
        CurrentJobId = jobId;

        _cancellation = new CancellationTokenSource();
        var progress = new Progress<FileScanProgress>(UpdateProgress);

        try
        {
            await _jobRunner.RunAsync(
                path,
                LargeFileThresholdBytes,
                progress,
                _cancellation.Token,
                jobId);
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
            await LoadRecentJobsAsync();
            await RefreshImportDataAsync();
            await LoadFolderTreeAsync();
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

    public async Task LoadRecentJobsAsync()
    {
        await _jobStore.InitializeAsync();
        var jobs = await _jobStore.GetRecentJobsAsync(10);

        RecentJobs.Clear();
        foreach (var job in jobs)
        {
            RecentJobs.Add(new JobSummaryView(job));
        }
    }

    public async Task LoadFolderTreeAsync()
    {
        if (string.IsNullOrWhiteSpace(CurrentJobId))
        {
            return;
        }

        await _jobStore.InitializeAsync();
        var root = await _jobStore.GetRootFolderAsync(CurrentJobId);
        if (root is null)
        {
            return;
        }

        UpdateOnUi(() =>
        {
            FolderRoots.Clear();
            var rootNode = new FolderNodeViewModel(_folderProvider, UpdateOnUi, CurrentJobId, root, null, 200);
            FolderRoots.Add(rootNode);
            _selectedFolderNode = rootNode;
            _selectedFolderPath = rootNode.FullPath;
            OnPropertyChanged(nameof(SelectedFolderPath));
        });

        await RefreshSelectedFolderSummaryAsync();
        await RefreshImportSelectionCountsAsync();
    }

    public async Task ExpandFolderNodeAsync(FolderNodeViewModel node)
    {
        await node.EnsureChildrenLoadedAsync(CancellationToken.None);
    }

    public void SelectFolderNode(FolderNodeViewModel? node)
    {
        _selectedFolderNode = node;
        _selectedFolderPath = node?.FullPath ?? "Select a folder.";
        OnPropertyChanged(nameof(SelectedFolderPath));
        _ = RefreshSelectedFolderSummaryAsync();
    }

    public async Task StartImportAsync()
    {
        if (IsImportRunning)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(CurrentJobId))
        {
            StatusText = "Select a job before starting import.";
            return;
        }

        IsImportRunning = true;
        IsImportPaused = false;
        StatusText = "Import started.";

        _importCancellation = new CancellationTokenSource();
        _importPipeline = new ImportPipeline(
            _jobStore,
            new DryRunUploader(_importRandom, new SystemClock()),
            new SystemClock(),
            new SerilogPipelineLogger());

        var progress = new Progress<TransferUpdate>(_ =>
        {
            QueueImportRefresh();
        });

        try
        {
            await Task.Run(() =>
                _importPipeline.RunAsync(CurrentJobId, MaxConcurrency, DelayMsBetweenStarts, progress, _importCancellation.Token),
                _importCancellation.Token);
            StatusText = "Import complete.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Import canceled.";
        }
        catch (Exception ex)
        {
            StatusText = $"Import failed: {ex.Message}";
        }
        finally
        {
            _importCancellation.Dispose();
            _importCancellation = null;
            IsImportRunning = false;
            IsImportPaused = false;
            await RefreshImportDataAsync();
        }
    }

    public void PauseImport()
    {
        if (!CanPauseImport)
        {
            return;
        }

        _importPipeline?.Pause();
        IsImportPaused = true;
        StatusText = "Import paused.";
    }

    public void ResumeImport()
    {
        if (!CanResumeImport)
        {
            return;
        }

        _importPipeline?.Resume();
        IsImportPaused = false;
        StatusText = "Import resumed.";
    }

    public void CancelImport()
    {
        if (!CanCancelImport)
        {
            return;
        }

        _importCancellation?.Cancel();
    }

    private void QueueImportRefresh()
    {
        lock (_importRefreshLock)
        {
            if (_importRefreshPending)
            {
                return;
            }

            _importRefreshPending = true;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(150);
            await RefreshImportDataAsync();
            lock (_importRefreshLock)
            {
                _importRefreshPending = false;
            }
        });
    }

    private async Task RefreshImportDataAsync()
    {
        if (string.IsNullOrWhiteSpace(CurrentJobId))
        {
            return;
        }

        var jobId = CurrentJobId;
        await _jobStore.InitializeAsync();

        var counts = await _jobStore.GetTransferCountsAsync(jobId);
        var files = await _jobStore.GetFilesForJobAsync(jobId);
        var transfers = await _jobStore.GetLatestTransfersAsync(jobId, 50);

        UpdateOnUi(() =>
        {
            _importTotalFiles = files.Count;
            _importQueued = counts.Queued;
            _importRunning = counts.Running;
            _importSucceeded = counts.Succeeded;
            _importFailed = counts.Failed;
            _importCanceled = counts.Canceled;

            OnPropertyChanged(nameof(ImportTotalFilesDisplay));
            OnPropertyChanged(nameof(ImportQueuedDisplay));
            OnPropertyChanged(nameof(ImportRunningDisplay));
            OnPropertyChanged(nameof(ImportSucceededDisplay));
            OnPropertyChanged(nameof(ImportFailedDisplay));
            OnPropertyChanged(nameof(ImportCanceledDisplay));

            LatestTransfers.Clear();
            foreach (var transfer in transfers)
            {
                LatestTransfers.Add(new TransferView(transfer));
            }
        });
    }

    private async Task RefreshSelectedFolderSummaryAsync()
    {
        if (_selectedFolderNode is null)
        {
            return;
        }

        var summary = await _jobStore.GetFolderSummaryAsync(_selectedFolderNode.FolderId);
        UpdateOnUi(() =>
        {
            _selectedFolderFiles = summary.totalFiles;
            _selectedFolderBytes = summary.totalBytes;
            _selectedFolderLargeFiles = summary.largeFiles;
            _selectedFolderExcludedFolders = summary.excludedFolders;

            OnPropertyChanged(nameof(SelectedFolderFilesDisplay));
            OnPropertyChanged(nameof(SelectedFolderBytesDisplay));
            OnPropertyChanged(nameof(SelectedFolderLargeFilesDisplay));
            OnPropertyChanged(nameof(SelectedFolderExcludedDisplay));
        });

        await RefreshImportSelectionCountsAsync();
    }

    private async Task RefreshImportSelectionCountsAsync()
    {
        if (string.IsNullOrWhiteSpace(CurrentJobId))
        {
            return;
        }

        var counts = await _jobStore.GetImportSelectionCountsAsync(CurrentJobId);
        UpdateOnUi(() =>
        {
            _includedFilesCount = counts.included;
            _excludedFilesCount = counts.excluded;
            OnPropertyChanged(nameof(IncludedFilesCountDisplay));
            OnPropertyChanged(nameof(ExcludedFilesCountDisplay));
        });
    }

    public async Task IncludeSelectedFolderAsync()
    {
        if (_selectedFolderNode is null)
        {
            return;
        }

        await _selectedFolderNode.ToggleIncludeAsync(CancellationToken.None);
        await RefreshSelectedFolderSummaryAsync();
    }

    public async Task ExcludeSelectedFolderAsync()
    {
        if (_selectedFolderNode is null)
        {
            return;
        }

        await _selectedFolderNode.ToggleExcludeAsync(CancellationToken.None);
        await RefreshSelectedFolderSummaryAsync();
    }

    public async Task ClearSelectedFolderOverrideAsync()
    {
        if (_selectedFolderNode is null)
        {
            return;
        }

        await _selectedFolderNode.ClearOverrideAsync(CancellationToken.None);
        await RefreshSelectedFolderSummaryAsync();
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

    private void UpdateOnUi(Action action)
    {
        if (_uiContext is null)
        {
            action();
            return;
        }

        _uiContext.Post(_ => action(), null);
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

public sealed class JobSummaryView
{
    public JobSummaryView(JobSummary summary)
    {
        JobId = summary.JobId;
        JobIdShort = summary.JobId.Length > 8 ? summary.JobId[..8] : summary.JobId;
        CreatedDisplay = summary.CreatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        SourceRoot = summary.SourceRoot;
        Status = summary.Status;
        FileCountDisplay = summary.FileCount.ToString("N0", CultureInfo.CurrentCulture);
        TotalBytesDisplay = FormatBytes(summary.TotalBytes);
        LargeWarningsDisplay = summary.LargeWarnings.ToString("N0", CultureInfo.CurrentCulture);
    }

    public string JobId { get; }

    public string JobIdShort { get; }

    public string CreatedDisplay { get; }

    public string SourceRoot { get; }

    public string Status { get; }

    public string FileCountDisplay { get; }

    public string TotalBytesDisplay { get; }

    public string LargeWarningsDisplay { get; }

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

public sealed class TransferView
{
    public TransferView(TransferSummary summary)
    {
        RelativePath = string.IsNullOrWhiteSpace(summary.RelativePath) ? summary.FileId : summary.RelativePath;
        Status = summary.Status;
        Attempt = summary.Attempt.ToString(CultureInfo.CurrentCulture);
        DurationDisplay = summary.DurationMs.HasValue
            ? $"{summary.DurationMs.Value} ms"
            : "--";
        Error = summary.Error ?? string.Empty;
    }

    public string RelativePath { get; }

    public string Status { get; }

    public string Attempt { get; }

    public string DurationDisplay { get; }

    public string Error { get; }
}
