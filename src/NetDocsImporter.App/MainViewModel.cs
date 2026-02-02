using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using NetDocsImporter.Core;
using NetDocsImporter.Data;

namespace NetDocsImporter.App;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private const long LargeFileThresholdBytes = 1_800_000_000;
    private const int FilePreviewLimit = 2000;

    private string? _selectedFolder;
    private long _totalFiles;
    private long _totalBytes;
    private bool _isScanning;
    private string _statusText = "Ready.";
    private string? _currentJobId;
    private string _currentJobSourceRoot = string.Empty;
    private string _currentJobState = "Ready";
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
    private string _importThroughput = "--";
    private DateTime? _importStartedUtc;
    private string _selectedFolderPath = "Select a folder.";
    private string _selectedFolderRelativePath = string.Empty;
    private long _selectedFolderFiles;
    private long _selectedFolderBytes;
    private long _selectedFolderLargeFiles;
    private long _selectedFolderExcludedFolders;
    private long _includedFilesCount;
    private long _excludedFilesCount;
    private string _selectedImportMode = "inherit";
    private string _selectedEffectiveImportMode = "include";
    private string _selectedProfileMode = "inherit";
    private string _selectedProfileSource = "Inherited";
    private string _treeSearchText = string.Empty;
    private string _fileSearchText = string.Empty;
    private string _selectedFileFilter = "All";
    private ProfileFieldView? _selectedProfileField;
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
    private readonly object _profileSaveLock = new();
    private bool _profileSavePending;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<LargeFileView> LargeFiles { get; } = new();
    public ObservableCollection<JobSummaryView> RecentJobs { get; } = new();
    public ObservableCollection<TransferView> LatestTransfers { get; } = new();
    public ObservableCollection<TreeNodeBase> FolderRoots { get; } = new();
    public ObservableCollection<ProfileFieldView> ProfileFields { get; } = new();
    public ObservableCollection<FileRowView> FolderFiles { get; } = new();

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

    public string CurrentJobSourceRoot
    {
        get => _currentJobSourceRoot;
        private set => SetField(ref _currentJobSourceRoot, value);
    }

    public string CurrentJobState
    {
        get => _currentJobState;
        private set => SetField(ref _currentJobState, value);
    }

    public JobSummaryView? SelectedRecentJob
    {
        get => _selectedRecentJob;
        set
        {
            if (SetField(ref _selectedRecentJob, value) && value is not null)
            {
                CurrentJobId = value.JobId;
                _ = LoadJobHeaderAsync();
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

    public string ImportThroughputDisplay => _importThroughput;

    public string SelectedFolderPath => _selectedFolderPath;

    public string SelectedFolderRelativePath => _selectedFolderRelativePath;

    public string SelectedFolderFilesDisplay => _selectedFolderFiles.ToString("N0", CultureInfo.CurrentCulture);

    public string SelectedFolderBytesDisplay => FormatBytes(_selectedFolderBytes);

    public string SelectedFolderLargeFilesDisplay => _selectedFolderLargeFiles.ToString("N0", CultureInfo.CurrentCulture);

    public string SelectedFolderExcludedDisplay => _selectedFolderExcludedFolders.ToString("N0", CultureInfo.CurrentCulture);

    public string IncludedFilesCountDisplay => _includedFilesCount.ToString("N0", CultureInfo.CurrentCulture);

    public string ExcludedFilesCountDisplay => _excludedFilesCount.ToString("N0", CultureInfo.CurrentCulture);

    public string SelectedImportMode => _selectedImportMode;

    public string SelectedEffectiveImportMode => _selectedEffectiveImportMode;

    public string SelectedProfileMode
    {
        get => _selectedProfileMode;
        private set => SetField(ref _selectedProfileMode, value);
    }

    public string SelectedProfileSource
    {
        get => _selectedProfileSource;
        private set => SetField(ref _selectedProfileSource, value);
    }

    public string TreeSearchText
    {
        get => _treeSearchText;
        set
        {
            if (SetField(ref _treeSearchText, value))
            {
                ApplyTreeFilter();
            }
        }
    }

    public string FileSearchText
    {
        get => _fileSearchText;
        set
        {
            if (SetField(ref _fileSearchText, value))
            {
                _ = RefreshFolderFilesAsync();
            }
        }
    }

    public string SelectedFileFilter
    {
        get => _selectedFileFilter;
        set
        {
            if (SetField(ref _selectedFileFilter, value))
            {
                _ = RefreshFolderFilesAsync();
            }
        }
    }

    public ProfileFieldView? SelectedProfileField
    {
        get => _selectedProfileField;
        set => SetField(ref _selectedProfileField, value);
    }

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

        ProfileFields.CollectionChanged += OnProfileFieldsChanged;
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
        CurrentJobState = "Scanning";

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
            CurrentJobState = "Ready";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Scan canceled.";
            CurrentJobState = "Ready";
        }
        catch (Exception ex)
        {
            StatusText = $"Scan failed: {ex.Message}";
            CurrentJobState = "Ready";
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            IsScanning = false;
            await LoadRecentJobsAsync();
            await LoadJobHeaderAsync();
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

    public async Task LoadJobHeaderAsync()
    {
        if (string.IsNullOrWhiteSpace(CurrentJobId))
        {
            return;
        }

        var job = await _jobStore.GetJobAsync(CurrentJobId);
        if (job is null)
        {
            return;
        }

        CurrentJobSourceRoot = job.SourceRoot;
        CurrentJobState = IsImportPaused ? "Paused" : IsImportRunning ? "Importing" : IsScanning ? "Scanning" : "Ready";
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
            _selectedFolderRelativePath = string.IsNullOrWhiteSpace(rootNode.RelativePath) ? "." : rootNode.RelativePath;
            OnPropertyChanged(nameof(SelectedFolderPath));
            OnPropertyChanged(nameof(SelectedFolderRelativePath));
        });

        ApplyTreeFilter();
        await RefreshSelectedFolderSummaryAsync();
        await RefreshImportSelectionCountsAsync();
        await RefreshFolderFilesAsync();
    }

    public async Task ExpandFolderNodeAsync(FolderNodeViewModel node)
    {
        await node.EnsureChildrenLoadedAsync(CancellationToken.None);
        ApplyTreeFilter();
    }

    public void SelectFolderNode(FolderNodeViewModel? node)
    {
        _selectedFolderNode = node;
        _selectedFolderPath = node?.FullPath ?? "Select a folder.";
        var relative = node?.RelativePath ?? string.Empty;
        _selectedFolderRelativePath = string.IsNullOrWhiteSpace(relative) ? "." : relative;
        OnPropertyChanged(nameof(SelectedFolderPath));
        OnPropertyChanged(nameof(SelectedFolderRelativePath));
        _ = RefreshSelectedFolderSummaryAsync();
        _ = RefreshFolderFilesAsync();
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
        _importStartedUtc = DateTime.UtcNow;
        CurrentJobState = "Importing";
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
            CurrentJobState = "Completed";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Import canceled.";
            CurrentJobState = "Ready";
        }
        catch (Exception ex)
        {
            StatusText = $"Import failed: {ex.Message}";
            CurrentJobState = "Ready";
        }
        finally
        {
            _importCancellation.Dispose();
            _importCancellation = null;
            IsImportRunning = false;
            IsImportPaused = false;
            _importStartedUtc = null;
            _importThroughput = "--";
            OnPropertyChanged(nameof(ImportThroughputDisplay));
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
        CurrentJobState = "Paused";
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
        CurrentJobState = "Importing";
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

    public void OpenLogsFolder()
    {
        OpenFolder(_paths.LogsDirectory);
    }

    public void OpenReportsFolder()
    {
        OpenFolder(_paths.ReportsDirectory);
    }

    private void OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch
        {
        }
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

            if (IsImportRunning && _importStartedUtc.HasValue)
            {
                var elapsed = DateTime.UtcNow - _importStartedUtc.Value;
                if (elapsed.TotalSeconds >= 1)
                {
                    var rate = _importSucceeded / elapsed.TotalMinutes;
                    _importThroughput = $"{rate:0.0} files/min";
                }
            }
            else
            {
                _importThroughput = "--";
            }

            OnPropertyChanged(nameof(ImportTotalFilesDisplay));
            OnPropertyChanged(nameof(ImportQueuedDisplay));
            OnPropertyChanged(nameof(ImportRunningDisplay));
            OnPropertyChanged(nameof(ImportSucceededDisplay));
            OnPropertyChanged(nameof(ImportFailedDisplay));
            OnPropertyChanged(nameof(ImportCanceledDisplay));
            OnPropertyChanged(nameof(ImportThroughputDisplay));

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
        var effectiveProfile = await _jobStore.GetEffectiveFolderProfilePayloadAsync(_selectedFolderNode.FolderId);
        var currentProfile = await _jobStore.GetFolderProfilePayloadAsync(_selectedFolderNode.FolderId);

        UpdateOnUi(() =>
        {
            _selectedFolderFiles = summary.totalFiles;
            _selectedFolderBytes = summary.totalBytes;
            _selectedFolderLargeFiles = summary.largeFiles;
            _selectedFolderExcludedFolders = summary.excludedFolders;
            _selectedImportMode = _selectedFolderNode.ImportMode;
            _selectedEffectiveImportMode = _selectedFolderNode.EffectiveImportMode;
            SelectedProfileMode = _selectedFolderNode.ProfileMode;
            SelectedProfileSource = SelectedProfileMode == "override" ? "Override" : "Inherited";

            OnPropertyChanged(nameof(SelectedFolderFilesDisplay));
            OnPropertyChanged(nameof(SelectedFolderBytesDisplay));
            OnPropertyChanged(nameof(SelectedFolderLargeFilesDisplay));
            OnPropertyChanged(nameof(SelectedFolderExcludedDisplay));
            OnPropertyChanged(nameof(SelectedImportMode));
            OnPropertyChanged(nameof(SelectedEffectiveImportMode));
        });

        LoadProfileFields(currentProfile ?? effectiveProfile);
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

    private async Task RefreshFolderFilesAsync()
    {
        if (_selectedFolderNode is null || string.IsNullOrWhiteSpace(CurrentJobId))
        {
            FolderFiles.Clear();
            return;
        }

        var files = await _jobStore.GetChildFilesAsync(CurrentJobId, _selectedFolderNode.FolderId, FilePreviewLimit);
        var effectiveIncluded = _selectedFolderNode.EffectiveIncluded;
        var filter = SelectedFileFilter;
        var search = FileSearchText?.Trim();

        var filtered = files
            .Where(f => string.IsNullOrWhiteSpace(search) || f.FullPath.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Where(f => filter switch
            {
                "Included" => effectiveIncluded,
                "Excluded" => !effectiveIncluded,
                "Large" => f.IsLargeWarning,
                _ => true
            })
            .Select(f => new FileRowView(f, effectiveIncluded))
            .ToList();

        UpdateOnUi(() =>
        {
            FolderFiles.Clear();
            foreach (var file in filtered)
            {
                FolderFiles.Add(file);
            }
        });
    }

    public async Task SetSelectedFolderImportModeAsync(string mode)
    {
        if (_selectedFolderNode is null)
        {
            return;
        }

        await _jobStore.UpdateFolderImportModeAsync(_selectedFolderNode.FolderId, mode);
        _selectedFolderNode.SetImportMode(mode);
        await RefreshSelectedFolderSummaryAsync();
        await RefreshFolderFilesAsync();
    }

    public async Task ApplyImportModeToChildrenAsync()
    {
        if (_selectedFolderNode is null)
        {
            return;
        }

        await _jobStore.ApplyImportModeToDescendantsAsync(_selectedFolderNode.JobId, _selectedFolderNode.FolderId, _selectedFolderNode.ImportMode);
        await RefreshSelectedFolderSummaryAsync();
    }

    public async Task SetSelectedProfileModeAsync(string mode)
    {
        if (_selectedFolderNode is null)
        {
            return;
        }

        await _jobStore.UpdateFolderProfileModeAsync(_selectedFolderNode.FolderId, mode);
        _selectedFolderNode.SetProfileMode(mode);
        SelectedProfileMode = mode;
        SelectedProfileSource = mode == "override" ? "Override" : "Inherited";
        await RefreshSelectedFolderSummaryAsync();
        QueueProfileSave();
    }

    public void AddProfileField()
    {
        ProfileFields.Add(new ProfileFieldView(string.Empty, string.Empty));
    }

    public void RemoveSelectedProfileField(ProfileFieldView? field)
    {
        if (field is null)
        {
            return;
        }

        ProfileFields.Remove(field);
    }

    public async Task ApplyProfileToChildrenAsync()
    {
        if (_selectedFolderNode is null)
        {
            return;
        }

        var payload = SerializeProfileFields();
        await _jobStore.ApplyProfileToDescendantsAsync(_selectedFolderNode.JobId, _selectedFolderNode.FolderId, payload);
    }

    private void LoadProfileFields(string? payloadJson)
    {
        ProfileFields.CollectionChanged -= OnProfileFieldsChanged;
        ProfileFields.Clear();

        if (!string.IsNullOrWhiteSpace(payloadJson))
        {
            try
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(payloadJson) ?? new Dictionary<string, string>();
                foreach (var pair in dict)
                {
                    var field = new ProfileFieldView(pair.Key, pair.Value);
                    field.PropertyChanged += OnProfileFieldPropertyChanged;
                    ProfileFields.Add(field);
                }
            }
            catch
            {
            }
        }

        ProfileFields.CollectionChanged += OnProfileFieldsChanged;
    }

    private string? SerializeProfileFields()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in ProfileFields)
        {
            if (string.IsNullOrWhiteSpace(field.Key))
            {
                continue;
            }

            dict[field.Key] = field.Value ?? string.Empty;
        }

        if (dict.Count == 0)
        {
            return null;
        }

        return JsonSerializer.Serialize(dict);
    }

    private void OnProfileFieldsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (ProfileFieldView field in e.NewItems)
            {
                field.PropertyChanged += OnProfileFieldPropertyChanged;
            }
        }

        if (e.OldItems is not null)
        {
            foreach (ProfileFieldView field in e.OldItems)
            {
                field.PropertyChanged -= OnProfileFieldPropertyChanged;
            }
        }

        QueueProfileSave();
    }

    private void OnProfileFieldPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        QueueProfileSave();
    }

    private void QueueProfileSave()
    {
        lock (_profileSaveLock)
        {
            if (_profileSavePending)
            {
                return;
            }

            _profileSavePending = true;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(200);
            await SaveProfileAsync();
            lock (_profileSaveLock)
            {
                _profileSavePending = false;
            }
        });
    }

    private async Task SaveProfileAsync()
    {
        if (_selectedFolderNode is null)
        {
            return;
        }

        if (!string.Equals(_selectedFolderNode.ProfileMode, "override", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var payload = SerializeProfileFields();
        await _jobStore.UpsertFolderProfileAsync(_selectedFolderNode.JobId, _selectedFolderNode.FolderId, payload);
    }

    private void ApplyTreeFilter()
    {
        foreach (var root in FolderRoots.OfType<FolderNodeViewModel>())
        {
            root.ApplyFilter(TreeSearchText);
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

public sealed class ProfileFieldView : INotifyPropertyChanged
{
    private string _key;
    private string _value;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ProfileFieldView(string key, string value)
    {
        _key = key;
        _value = value;
    }

    public string Key
    {
        get => _key;
        set
        {
            if (_key == value)
            {
                return;
            }

            _key = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Key)));
        }
    }

    public string Value
    {
        get => _value;
        set
        {
            if (_value == value)
            {
                return;
            }

            _value = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
        }
    }
}

public sealed class FileRowView
{
    public FileRowView(FileRecord record, bool effectiveIncluded)
    {
        Name = Path.GetFileName(record.FullPath);
        RelativePath = record.RelativePath;
        SizeDisplay = FormatBytes(record.SizeBytes);
        ModifiedDisplay = record.ModifiedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        LargeWarning = record.IsLargeWarning ? "Yes" : "No";
        EffectiveImport = effectiveIncluded ? "Yes" : "No";
    }

    public string Name { get; }

    public string RelativePath { get; }

    public string SizeDisplay { get; }

    public string ModifiedDisplay { get; }

    public string LargeWarning { get; }

    public string EffectiveImport { get; }

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
