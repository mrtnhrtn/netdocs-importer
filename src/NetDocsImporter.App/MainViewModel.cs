using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using NetDocsImporter.Core;
using NetDocsImporter.Core.Security;
using NetDocsImporter.Data;
using NetDocsImporter.NetDocs;

namespace NetDocsImporter.App;

public sealed partial class MainViewModel : INotifyPropertyChanged
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
    private int _maxConcurrency = 8;
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
    private bool _selectedFolderIsEffectivelyEmpty;
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
    private bool _hasFolderRoots;
    private bool _isPreExportWarningsBusy;
    private int _preExportLargeFileWarnings;
    private int _preExportEmptyFolderWarnings;
    private string _preExportWarningsStatus = "Select a folder to run checks.";
    private string _ndImportPath = string.Empty;
    private string _ndImportHost = "upload.au.netdocuments.com";
    private string _ndImportCabinet = string.Empty;
    private string _ndImportUsername = string.Empty;
    private string _ndImportPassword = string.Empty;
    private bool _rememberNdImportPassword;
    private bool _ndImportIncludePassword;
    private bool _ndImportUtf8 = true;
    private string _ndImportDateFormat = "DMY";
    private bool _ndImportNoValidation;
    private int _ndImportMaxErrors = 50;
    private string? _lastNdImportExportPath;
    private string _schemaPath = string.Empty;
    private string _schemaStatus = "No schema loaded.";
    private bool _schemaCabinetMatches;
    private string _schemaCabinetName = string.Empty;
    private bool _hasSchemaLoaded;
    private bool _isExportMode;
    private bool _isDarkMode;
    private string _exportDestinationRootPath = string.Empty;
    private bool _exportAllVersions;
    private bool _exportDownloadFiltersAsFolders = true;
    private bool _exportIncludeCustomAttributes;
    private ExportMetadataFormat _exportMetadataFormat = ExportMetadataFormat.Json;
    private NetDocumentsRegion _netDocumentsRegion = NetDocumentsRegion.AU;
    private readonly Dictionary<string, NetDocumentsOAuthClientConfig> _netDocumentsOAuthClientProfiles = new(StringComparer.OrdinalIgnoreCase);
    private NetDocumentsOAuthClientConfig? _selectedNetDocumentsOAuthClientConfig;
    private string _netDocumentsBootstrapClientId = string.Empty;
    private string _netDocumentsBootstrapClientSecret = string.Empty;
    private string _netDocumentsBootstrapRedirectUri = NetDocumentsRegionDefaults.DefaultRedirectUri;
    private AppSettings _settings = new();
    private ProfileSchemaCatalog? _schemaCatalog;
    private ProfileSchemaDictionary? _schema;
    private readonly HashSet<string> _refreshedCabinetSchemaThisSession = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _settingsSaveLock = new();
    private bool _settingsSavePending;
    private StepItem? _currentStep;
    private bool _isSettingsOpen;
    private CancellationTokenSource? _cancellation;
    private CancellationTokenSource? _importCancellation;
    private readonly AppPaths _paths;
    private readonly CompletedJobLogStore _completedJobLogStore;
    private readonly SecretStore _secretStore;
    private readonly INetDocumentsOAuthClientConfigProvider _netDocumentsOAuthClientConfigProvider;
    private readonly AppRuntimeOptions _runtimeOptions;
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
    private int _recentJobsRefreshInFlight;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<LargeFileView> LargeFiles { get; } = new();
    public ObservableCollection<JobSummaryView> RecentJobs { get; } = new();
    public ObservableCollection<TransferView> LatestTransfers { get; } = new();
    public ObservableCollection<TreeNodeBase> FolderRoots { get; } = new();
    public ObservableCollection<ProfileFieldView> ProfileFields { get; } = new();
    public ObservableCollection<FileRowView> FolderFiles { get; } = new();
    public ObservableCollection<PreExportWarningView> PreExportWarnings { get; } = new();
    public ObservableCollection<NdImportSessionView> NdImportSessions { get; } = new();
    public ObservableCollection<StepItem> Steps { get; } = new();
    public IReadOnlyList<NetDocumentsRegion> NetDocumentsRegions { get; } = Enum.GetValues<NetDocumentsRegion>();
    public IReadOnlyList<string> NdImportDateFormatOptions { get; } = new[] { "DMY", "YMD", "MDY" };

    public bool HasFolderRoots
    {
        get => _hasFolderRoots;
        private set
        {
            if (SetField(ref _hasFolderRoots, value))
            {
                OnPropertyChanged(nameof(ShowReviewTargetProfileContext));
            }
        }
    }

    public bool ShowReviewTargetProfileContext => ShowExportContext || HasFolderRoots;

    public bool IsPreExportWarningsBusy
    {
        get => _isPreExportWarningsBusy;
        private set => SetField(ref _isPreExportWarningsBusy, value);
    }

    public string PreExportLargeFileWarningsDisplay => _preExportLargeFileWarnings.ToString("N0", CultureInfo.CurrentCulture);

    public string PreExportEmptyFolderWarningsDisplay => _preExportEmptyFolderWarnings.ToString("N0", CultureInfo.CurrentCulture);

    public bool HasPreExportWarnings => PreExportWarnings.Count > 0;

    public string PreExportWarningsStatus
    {
        get => _preExportWarningsStatus;
        private set => SetField(ref _preExportWarningsStatus, value);
    }

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
            var previousJobId = _currentJobId;
            if (SetField(ref _currentJobId, value))
            {
                OnPropertyChanged(nameof(CanStartImport));
                OnPropertyChanged(nameof(CanPickNetDocumentsTarget));
                OnPropertyChanged(nameof(CanConfirmNetDocumentsTarget));
                OnPropertyChanged(nameof(CanContinueToReviewScope));
                OnPropertyChanged(nameof(CanRunDirectUpload));
                RaiseDirectUploadQueueAvailabilityChanged();

                if (!string.Equals(previousJobId, value, StringComparison.OrdinalIgnoreCase))
                {
                    HandleDirectUploadContextChanged(
                        "Source job changed. Refresh direct upload preflight.",
                        refreshPreflight: false);
                    HandleExportContextChanged(
                        "Source job changed. Refresh export preflight.",
                        refreshPreflight: false);
                }
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

    public bool SelectedFolderIsEffectivelyEmpty => _selectedFolderIsEffectivelyEmpty;

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

    public StepItem? CurrentStep
    {
        get => _currentStep;
        set => SetField(ref _currentStep, value);
    }

    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        set => SetField(ref _isSettingsOpen, value);
    }

    public bool IsAuthenticationRequired => !IsNetDocumentsConnected;

    public bool CanAccessMainFlow => IsNetDocumentsConnected;

    public string SchemaPath
    {
        get => _schemaPath;
        private set => SetField(ref _schemaPath, value);
    }

    public string SchemaStatus
    {
        get => _schemaStatus;
        private set => SetField(ref _schemaStatus, value);
    }

    public bool SchemaCabinetMatches
    {
        get => _schemaCabinetMatches;
        private set => SetField(ref _schemaCabinetMatches, value);
    }

    public string SchemaCabinetName
    {
        get => _schemaCabinetName;
        private set => SetField(ref _schemaCabinetName, value);
    }

    public bool HasSchemaLoaded
    {
        get => _hasSchemaLoaded;
        private set => SetField(ref _hasSchemaLoaded, value);
    }

    public NetDocumentsRegion SelectedNetDocumentsRegion
    {
        get => _netDocumentsRegion;
        set
        {
            if (SetField(ref _netDocumentsRegion, value))
            {
                InvalidateTargetBrowserContext("region-changed");
                _refreshedCabinetSchemaThisSession.Clear();
                var settings = GetOrCreateNetDocumentsSettings();
                NetDocumentsRegionDefaults.EnsureDefaults(settings);
                settings.Region = value;
                ResolveNetDocumentsOAuthClientConfig();
                OnPropertyChanged(nameof(NetDocumentsConnectionProfileStatus));
                OnPropertyChanged(nameof(CanConnectToNetDocuments));
                OnPropertyChanged(nameof(CanShowNetDocumentsDevBootstrap));
                OnPropertyChanged(nameof(CanSaveNetDocumentsDevBootstrap));
                QueueSettingsSave();
                _ = LoadNetDocumentsMetadataAsync();
            }
        }
    }

    public bool IsDeveloperMode => _runtimeOptions.IsDeveloperMode;

    public string NetDocumentsConnectionProfileStatus =>
        _selectedNetDocumentsOAuthClientConfig is null
            ? "OAuth profile for this region is not installed. Contact administrator."
            : $"OAuth profile configured for {SelectedNetDocumentsRegion}.";

    public bool CanConnectToNetDocuments
    {
        get
        {
            if (_selectedNetDocumentsOAuthClientConfig is null ||
                string.IsNullOrWhiteSpace(_selectedNetDocumentsOAuthClientConfig.ClientId) ||
                string.IsNullOrWhiteSpace(_selectedNetDocumentsOAuthClientConfig.RedirectUri))
            {
                return false;
            }

            return !string.IsNullOrWhiteSpace(_selectedNetDocumentsOAuthClientConfig.ApiBaseUrl)
                && !string.IsNullOrWhiteSpace(_selectedNetDocumentsOAuthClientConfig.OAuthAuthorizeBaseUrl)
                && !string.IsNullOrWhiteSpace(_selectedNetDocumentsOAuthClientConfig.OAuthTokenUrl);
        }
    }

    public bool CanShowNetDocumentsDevBootstrap => IsDeveloperMode;

    public string NetDocumentsBootstrapClientId
    {
        get => _netDocumentsBootstrapClientId;
        set
        {
            if (SetField(ref _netDocumentsBootstrapClientId, value))
            {
                OnPropertyChanged(nameof(CanSaveNetDocumentsDevBootstrap));
            }
        }
    }

    public string NetDocumentsBootstrapClientSecret
    {
        get => _netDocumentsBootstrapClientSecret;
        set
        {
            if (SetField(ref _netDocumentsBootstrapClientSecret, value))
            {
                OnPropertyChanged(nameof(CanSaveNetDocumentsDevBootstrap));
            }
        }
    }

    public string NetDocumentsBootstrapRedirectUri
    {
        get => _netDocumentsBootstrapRedirectUri;
        set
        {
            if (SetField(ref _netDocumentsBootstrapRedirectUri, value))
            {
                OnPropertyChanged(nameof(CanSaveNetDocumentsDevBootstrap));
            }
        }
    }

    public bool CanSaveNetDocumentsDevBootstrap =>
        IsDeveloperMode &&
        !string.IsNullOrWhiteSpace(NetDocumentsBootstrapClientId) &&
        !string.IsNullOrWhiteSpace(NetDocumentsBootstrapRedirectUri);

    public string NdImportPath
    {
        get => _ndImportPath;
        set
        {
            var normalized = NormalizeNdImportPath(value);
            if (SetField(ref _ndImportPath, normalized))
            {
                OnPropertyChanged(nameof(CanLaunchNdImport));
                QueueSettingsSave();
            }
        }
    }

    public string NdImportHost
    {
        get => _ndImportHost;
        set
        {
            if (SetField(ref _ndImportHost, value))
            {
                QueueSettingsSave();
            }
        }
    }

    public string NdImportCabinet
    {
        get => _ndImportCabinet;
        set
        {
            if (SetField(ref _ndImportCabinet, value))
            {
                UpdateSchemaMatch();
                QueueSettingsSave();
            }
        }
    }

    public string NdImportUsername
    {
        get => _ndImportUsername;
        set
        {
            if (SetField(ref _ndImportUsername, value))
            {
                QueueSettingsSave();
            }
        }
    }

    public string NdImportPassword
    {
        get => _ndImportPassword;
        set
        {
            if (SetField(ref _ndImportPassword, value) && RememberNdImportPassword)
            {
                QueueSettingsSave();
            }
        }
    }

    public bool RememberNdImportPassword
    {
        get => _rememberNdImportPassword;
        set
        {
            if (SetField(ref _rememberNdImportPassword, value))
            {
                QueueSettingsSave();
                if (!value)
                {
                    _secretStore.DeleteSecret(GetPasswordSecretName());
                }
            }
        }
    }

    public bool NdImportIncludePassword
    {
        get => _ndImportIncludePassword;
        set => SetField(ref _ndImportIncludePassword, value);
    }

    public bool NdImportUtf8
    {
        get => _ndImportUtf8;
        set => SetField(ref _ndImportUtf8, value);
    }

    public string NdImportDateFormat
    {
        get => _ndImportDateFormat;
        set
        {
            var normalized = NormalizeNdImportDateFormat(value);
            if (SetField(ref _ndImportDateFormat, normalized))
            {
                QueueSettingsSave();
            }
        }
    }

    public bool NdImportNoValidation
    {
        get => _ndImportNoValidation;
        set => SetField(ref _ndImportNoValidation, value);
    }

    public int NdImportMaxErrors
    {
        get => _ndImportMaxErrors;
        set => SetField(ref _ndImportMaxErrors, value);
    }

    public bool CanLaunchNdImport =>
        !string.IsNullOrWhiteSpace(NdImportPath) &&
        File.Exists(NdImportPath) &&
        !string.IsNullOrWhiteSpace(_lastNdImportExportPath) &&
        File.Exists(_lastNdImportExportPath);

    public bool IsExportMode
    {
        get => _isExportMode;
        set
        {
            if (SetField(ref _isExportMode, value))
            {
                OnPropertyChanged(nameof(IsImportMode));
                OnPropertyChanged(nameof(ShowImportContext));
                OnPropertyChanged(nameof(ShowExportContext));
                OnPropertyChanged(nameof(ShowReviewTargetProfileContext));
                OnPropertyChanged(nameof(JobOverviewTitle));
                OnPropertyChanged(nameof(ExportModeToggleText));
                OnPropertyChanged(nameof(CanRefreshExportPreflight));
                OnPropertyChanged(nameof(CanRunExport));
                OnPropertyChanged(nameof(CanCancelExport));
                HandleExportContextChanged("Mode changed.", refreshPreflight: true);
                QueueSettingsSave();
            }
        }
    }

    public bool IsImportMode => !IsExportMode;

    public bool ShowImportContext => IsImportMode;

    public bool ShowExportContext => IsExportMode;

    public string JobOverviewTitle => IsExportMode ? "Export Job Overview" : "Import Job Overview";

    public string ExportModeToggleText => IsExportMode ? "Import" : "Export mode";

    public string ExportDestinationRootPath
    {
        get => _exportDestinationRootPath;
        set
        {
            if (SetField(ref _exportDestinationRootPath, value))
            {
                QueueSettingsSave();
            }
        }
    }

    public bool ExportAllVersions
    {
        get => _exportAllVersions;
        set
        {
            if (SetField(ref _exportAllVersions, value))
            {
                QueueSettingsSave();
            }
        }
    }

    public bool ExportDownloadFiltersAsFolders
    {
        get => _exportDownloadFiltersAsFolders;
        set
        {
            if (SetField(ref _exportDownloadFiltersAsFolders, value))
            {
                QueueSettingsSave();
            }
        }
    }

    public bool ExportIncludeCustomAttributes
    {
        get => _exportIncludeCustomAttributes;
        set
        {
            if (SetField(ref _exportIncludeCustomAttributes, value))
            {
                QueueSettingsSave();
            }
        }
    }

    public ExportMetadataFormat ExportMetadataFormat
    {
        get => _exportMetadataFormat;
        set
        {
            if (SetField(ref _exportMetadataFormat, value))
            {
                QueueSettingsSave();
            }
        }
    }

    public bool IsDarkMode
    {
        get => _isDarkMode;
        set
        {
            if (SetField(ref _isDarkMode, value))
            {
                ThemeManager.ApplyTheme(_isDarkMode);
                QueueSettingsSave();
            }
        }
    }

    public void ToggleExportMode()
    {
        IsExportMode = !IsExportMode;
    }

    public MainViewModel()
        : this(new AppRuntimeOptions())
    {
    }

    public MainViewModel(AppRuntimeOptions runtimeOptions)
    {
        _runtimeOptions = runtimeOptions ?? new AppRuntimeOptions();
        _paths = new AppPaths();
        _completedJobLogStore = new CompletedJobLogStore(_paths.CompletedJobsDirectory, TimeSpan.FromDays(30));
        _completedJobLogStore.PruneExpired(DateTime.UtcNow);
        _secretStore = new SecretStore(_paths.SecretsDirectory);
        var userProfileProvider = new DpapiNetDocumentsOAuthClientConfigProvider(_secretStore, AppSettings.DefaultNetDocumentsOAuthClientProfilesRef);
        var machineProfilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "NetDocsImporter",
            "oauth-profiles.dat");
        var provisionedProfileProvider = new ProvisionedNetDocumentsOAuthClientConfigProvider(machineProfilePath);
        _netDocumentsOAuthClientConfigProvider = new CompositeNetDocumentsOAuthClientConfigProvider(provisionedProfileProvider, userProfileProvider);
        _jobStore = new JobStore(_paths.DatabasePath);
        _jobRunner = new ScanJobRunner(_jobStore);
        _uiContext = SynchronizationContext.Current;
        _folderProvider = new JobStoreFolderTreeProvider(_jobStore);

        ProfileFields.CollectionChanged += OnProfileFieldsChanged;
        FolderRoots.CollectionChanged += (_, _) => HasFolderRoots = FolderRoots.Count > 0;
        PreExportWarnings.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasPreExportWarnings));

        Steps.Add(new StepItem(1, StepKey.SelectFolder, "NetDocuments", "Choose your NetDocuments location", this));
        Steps.Add(new StepItem(2, StepKey.ReviewScope, "Local Folder", "Select local folder and plan", this));
        Steps.Add(new StepItem(3, StepKey.RecentJobs, "Recent jobs", "Review and select prior jobs", this));

        CurrentStep = Steps[0];
        InitializeNetDocumentsIntegration();
        InitializeReviewScopeNetDocuments();
    }
    public async Task SelectFolderAndScanAsync()
    {
        if (!CanSelectSourceFolder)
        {
            StatusText = "Connect to NetDocuments before selecting a source folder.";
            return;
        }

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
        _currentJobRepositoryId = string.Empty;
        OnPropertyChanged(nameof(CurrentJobRepositoryId));

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
                jobId,
                SelectedNetDocumentsRepositoryId);
            StatusText = "Scan complete.";
            CurrentJobState = "Ready";
            OnPropertyChanged(nameof(CanPickNetDocumentsTarget));
            OnPropertyChanged(nameof(CanConfirmNetDocumentsTarget));
            OnPropertyChanged(nameof(CanContinueToReviewScope));
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
            if (IsNetDocumentsConnected)
            {
                await LoadNetDocumentsTargetContainersAsync();
            }
            await RefreshReviewScopeNetDocumentsAsync();
            await RefreshPreExportWarningsAsync();
            if (IsDirectApiMode)
            {
                _ = RefreshDirectUploadPreflightAsync();
            }
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
        if (Interlocked.Exchange(ref _recentJobsRefreshInFlight, 1) == 1)
        {
            return;
        }

        try
        {
            await _jobStore.InitializeAsync();
            _completedJobLogStore.PruneExpired(DateTime.UtcNow);
            var jobs = await _jobStore.GetRecentJobsAsync(10);
            var latestRunByJob = await _completedJobLogStore.GetLatestRunsByJobAsync(50);

            RecentJobs.Clear();
            foreach (var job in jobs)
            {
                var view = new JobSummaryView(job);
                if (latestRunByJob.TryGetValue(job.JobId, out var latestRun))
                {
                    view.ApplyLatestRun(latestRun);
                }

                RecentJobs.Add(view);
            }

            await LoadQueueJobsAsync();
        }
        finally
        {
            Interlocked.Exchange(ref _recentJobsRefreshInFlight, 0);
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
        _currentJobRepositoryId = job.RepositoryId ?? string.Empty;
        OnPropertyChanged(nameof(CurrentJobRepositoryId));
        if (!string.IsNullOrWhiteSpace(_currentJobRepositoryId) &&
            !string.Equals(SelectedNetDocumentsRepositoryId, _currentJobRepositoryId, StringComparison.OrdinalIgnoreCase))
        {
            SelectedNetDocumentsRepositoryId = _currentJobRepositoryId;
        }

        _ = RefreshReviewScopeNetDocumentsAsync();
        _ = RefreshPreExportWarningsAsync();
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
            UpdateOnUi(() =>
            {
                FolderRoots.Clear();
                SelectFolderNode(null);
            });
            return;
        }

        UpdateOnUi(() =>
        {
            FolderRoots.Clear();
            var rootNode = new FolderNodeViewModel(_folderProvider, UpdateOnUi, CurrentJobId, root, null, 200);
            FolderRoots.Add(rootNode);
            rootNode.IsSelected = true;
            rootNode.IsExpanded = true;
            SelectFolderNode(rootNode);
        });

        ApplyTreeFilter();
        await RefreshFolderImportCountsAsync();
        await RefreshPreExportWarningsAsync();
    }

    private async Task RefreshFolderImportCountsAsync()
    {
        if (string.IsNullOrWhiteSpace(CurrentJobId))
        {
            return;
        }

        var counts = await _jobStore.GetFolderImportCountsForJobAsync(CurrentJobId);
        var map = counts.ToDictionary(c => c.FolderId, c => c, StringComparer.OrdinalIgnoreCase);

        UpdateOnUi(() =>
        {
            foreach (var root in FolderRoots.OfType<FolderNodeViewModel>())
            {
                root.ApplyImportCounts(map);
            }

            _selectedFolderIsEffectivelyEmpty = _selectedFolderNode?.IsEffectivelyEmpty ?? false;
            OnPropertyChanged(nameof(SelectedFolderIsEffectivelyEmpty));
        });
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
        _selectedFolderIsEffectivelyEmpty = node?.IsEffectivelyEmpty ?? false;
        OnPropertyChanged(nameof(SelectedFolderPath));
        OnPropertyChanged(nameof(SelectedFolderRelativePath));
        OnPropertyChanged(nameof(SelectedFolderIsEffectivelyEmpty));
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

    public async Task<NdImportExportResult?> ExportNdImportAsync(NdImportExportOptions options)
    {
        if (string.IsNullOrWhiteSpace(CurrentJobId))
        {
            StatusText = "Select a job before exporting for ndImport.";
            return null;
        }

        try
        {
            var exporter = new NdImportCsvExporter(_jobStore);
            var result = await exporter.ExportAsync(CurrentJobId, _paths.ReportsDirectory, options);
            StatusText = $"ndImport export created ({result.TotalFiles:N0} files).";
            return result;
        }
        catch (Exception ex)
        {
            StatusText = $"ndImport export failed: {ex.Message}";
            return null;
        }
    }

    public async Task LoadNdImportSettingsAsync()
    {
        await LoadSettingsAsync();
        await LoadNetDocumentsMetadataAsync();
        await TryRestoreNetDocumentsSessionAsync();
        if (IsNetDocumentsConnected)
        {
            await LoadNetDocumentsTargetContainersAsync();
        }
        await RefreshReviewScopeNetDocumentsAsync();
        await RefreshPreExportWarningsAsync();
        if (string.IsNullOrWhiteSpace(NdImportPath))
        {
            var localCandidate = Path.Combine(AppContext.BaseDirectory, "ndimport.exe");
            if (File.Exists(localCandidate))
            {
                NdImportPath = localCandidate;
            }
        }

        if (!string.IsNullOrWhiteSpace(SchemaPath) && File.Exists(SchemaPath))
        {
            await LoadSchemaAsync(SchemaPath);
        }

        if (!IsNetDocumentsConnected)
        {
            IsSettingsOpen = true;
            SetCurrentStep(StepKey.SelectFolder);
            StatusText = CanConnectToNetDocuments
                ? "Sign in to NetDocuments from Settings before continuing."
                : "OAuth profile for this region is not installed. Contact administrator.";
        }
    }

    public async Task ExportNdImportListAsync()
    {
        await EnsureExportSchemaReadyAsync();
        var expectedSyncedSchemaPath = string.IsNullOrWhiteSpace(SelectedNetDocumentsCabinetId)
            ? string.Empty
            : $"synced://{SelectedNetDocumentsCabinetId}";
        var exportSchema = _schema;
        if (!string.IsNullOrWhiteSpace(expectedSyncedSchemaPath) &&
            !string.Equals(SchemaPath, expectedSyncedSchemaPath, StringComparison.OrdinalIgnoreCase))
        {
            exportSchema = null;
        }

        var options = new NdImportExportOptions
        {
            IncludeAuditStamps = true,
            MappingMode = NdImportMappingMode.Mirror,
            AnchorFolderPath = string.Empty,
            ImportedBy = string.IsNullOrWhiteSpace(NdImportUsername) ? "Imported Content" : NdImportUsername,
            EffectiveProfileDefaults = EffectiveProfileDefaults,
            IncludeProfileMetadata = exportSchema is not null || EffectiveProfileDefaults.HasValues,
            ProfileSchema = exportSchema,
            IncludeAllCabinetAttributes = true,
            ExportLookupKeys = true,
            NdImportDateFormat = NdImportDateFormat
        };

        if (EffectiveProfileDefaults.HasValues)
        {
            options.IncludeProfileMetadata = true;
            System.Diagnostics.Trace.WriteLine($"Applying {EffectiveProfileDefaults.ValuesByAttributeId.Count} effective profile defaults to CSV export.");
        }

        var result = await ExportNdImportAsync(options);
        if (result is null)
        {
            return;
        }

        _lastNdImportExportPath = result.OutputPath;
        OnPropertyChanged(nameof(CanLaunchNdImport));
        NdImportSessions.Insert(0, new NdImportSessionView(DateTime.Now, "Exported", Path.GetFileName(result.OutputPath)));

        var message = string.Join(Environment.NewLine, new[]
        {
            $"Files exported: {result.TotalFiles:N0}",
            $"Large file warnings: {result.LargeFileWarnings:N0}",
            $"Empty folder warnings: {result.EmptyFolderWarnings:N0}",
            $"Unresolved field warnings: {result.UnresolvedFieldWarnings:N0}",
            $"Unresolved value warnings: {result.UnresolvedValueWarnings:N0}",
            $"File include overrides: {result.FileIncludeOverrides:N0}",
            $"File exclude overrides: {result.FileExcludeOverrides:N0}",
            $"Access denied warnings: {result.AccessDeniedWarnings:N0}",
            string.Empty,
            "Use only \"Export CSV\" with ndImport.exe.",
            string.Empty,
            $"Export CSV: {result.OutputPath}",
            $"Diagnostics report (not for ndImport): {result.WarningsPath}"
        });

        var openExport = new TaskDialogButton("Open ndImport CSV");
        var openWarnings = new TaskDialogButton("Open diagnostics report (not for ndImport)");
        var close = new TaskDialogButton("Close", true, true);

        var dialog = new TaskDialogPage
        {
            Caption = "ndImport export",
            Heading = "ndImport export created",
            Text = message,
            Buttons = { openExport, openWarnings, close }
        };

        var action = ShowTaskDialog(dialog);
        if (action == openExport)
        {
            OpenFile(result.OutputPath);
        }
        else if (action == openWarnings)
        {
            OpenFile(result.WarningsPath);
        }
    }

    private async Task EnsureExportSchemaReadyAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedNetDocumentsRepositoryId) ||
            string.IsNullOrWhiteSpace(SelectedNetDocumentsCabinetId))
        {
            return;
        }

        var refreshKey = BuildSchemaRefreshKey();
        var expectedSyncedSchemaPath = $"synced://{SelectedNetDocumentsCabinetId}";
        var hasMatchingSyncedSchema = _schema is not null &&
                                      string.Equals(SchemaPath, expectedSyncedSchemaPath, StringComparison.OrdinalIgnoreCase);

        if (IsNetDocumentsConnected && !_refreshedCabinetSchemaThisSession.Contains(refreshKey))
        {
            await SyncNetDocumentsAttributesAsync();
            hasMatchingSyncedSchema = _schema is not null &&
                                      string.Equals(SchemaPath, expectedSyncedSchemaPath, StringComparison.OrdinalIgnoreCase);
        }

        if (!hasMatchingSyncedSchema)
        {
            await LoadSchemaFromSyncedMetadataAsync();
            hasMatchingSyncedSchema = _schema is not null &&
                                      string.Equals(SchemaPath, expectedSyncedSchemaPath, StringComparison.OrdinalIgnoreCase);
        }

        if (hasMatchingSyncedSchema)
        {
            _refreshedCabinetSchemaThisSession.Add(refreshKey);
        }

        if (!hasMatchingSyncedSchema)
        {
            Trace.WriteLine(
                $"CSV export schema fallback: no synced schema available for repo='{SelectedNetDocumentsRepositoryId}' cabinet='{SelectedNetDocumentsCabinetId}'.");
        }
    }

    private string BuildSchemaRefreshKey()
    {
        return $"{SelectedNetDocumentsRegion}:{SelectedNetDocumentsRepositoryId}:{SelectedNetDocumentsCabinetId}";
    }

    public async Task LoadSchemaAsync(string path)
    {
        try
        {
            _schemaCatalog = await ProfileSchemaLoader.LoadAsync(path);
            _schema = _schemaCatalog.GetForCabinet(NdImportCabinet);
            SchemaPath = path;
            SchemaCabinetName = _schema?.CabinetName ?? string.Empty;
            SchemaStatus = _schema is null
                ? "No schema loaded."
                : string.IsNullOrWhiteSpace(_schema.SchemaVersion)
                    ? "Schema loaded."
                    : $"Schema loaded (version {_schema.SchemaVersion}).";
            HasSchemaLoaded = _schema is not null;
            UpdateSchemaMatch();
            ResolveProfileFieldHints();
            QueueSettingsSave();
        }
        catch (Exception ex)
        {
            SchemaStatus = $"Failed to load schema: {ex.Message}";
            HasSchemaLoaded = false;
        }
    }

    public Task ConnectToNetDocumentsAsync()
    {
        return ConnectToNetDocumentsCoreAsync();
    }

    public void OpenSettings()
    {
        IsSettingsOpen = true;
    }

    public void CloseSettings()
    {
        if (IsAuthenticationRequired)
        {
            return;
        }

        IsSettingsOpen = false;
    }

    public void ToggleSettings()
    {
        if (IsSettingsOpen)
        {
            CloseSettings();
            return;
        }

        OpenSettings();
    }

    private async Task ConnectToNetDocumentsCoreAsync()
    {
        await ConnectAndSyncNetDocumentsAsync();
        if (IsNetDocumentsConnected)
        {
            IsSettingsOpen = false;
        }
    }

    private async Task LoadSettingsAsync()
    {
        _settings = await AppSettings.LoadAsync(_paths.SettingsPath);
        _isDarkMode = IsDarkTheme(_settings.Theme);
        ThemeManager.ApplyTheme(_isDarkMode);
        OnPropertyChanged(nameof(IsDarkMode));

        if (!string.IsNullOrWhiteSpace(_settings.NdImportPath))
        {
            _ndImportPath = NormalizeNdImportPath(_settings.NdImportPath);
            OnPropertyChanged(nameof(NdImportPath));
            OnPropertyChanged(nameof(CanLaunchNdImport));
        }

        if (!string.IsNullOrWhiteSpace(_settings.NdImportHost))
        {
            _ndImportHost = _settings.NdImportHost;
            OnPropertyChanged(nameof(NdImportHost));
        }

        if (!string.IsNullOrWhiteSpace(_settings.NdImportCabinet))
        {
            _ndImportCabinet = _settings.NdImportCabinet;
            OnPropertyChanged(nameof(NdImportCabinet));
        }

        if (!string.IsNullOrWhiteSpace(_settings.NdImportUsername))
        {
            _ndImportUsername = _settings.NdImportUsername;
            OnPropertyChanged(nameof(NdImportUsername));
        }

        _ndImportDateFormat = NormalizeNdImportDateFormat(_settings.NdImportDateFormat);
        OnPropertyChanged(nameof(NdImportDateFormat));
        _selectedImportExecutionMode = ImportExecutionMode.DirectApi;
        OnPropertyChanged(nameof(SelectedImportExecutionMode));
        OnPropertyChanged(nameof(IsNdImportCsvMode));
        OnPropertyChanged(nameof(IsDirectApiMode));
        OnPropertyChanged(nameof(CanRunDirectUpload));
        RaiseDirectUploadQueueAvailabilityChanged();

        _rememberNdImportPassword = _settings.RememberNdImportPassword;
        OnPropertyChanged(nameof(RememberNdImportPassword));

        if (_rememberNdImportPassword)
        {
            var secretName = GetPasswordSecretName();
            var secret = await _secretStore.ReadSecretAsync(secretName);
            if (!string.IsNullOrWhiteSpace(secret))
            {
                _ndImportPassword = secret;
                OnPropertyChanged(nameof(NdImportPassword));
            }
        }

        if (!string.IsNullOrWhiteSpace(_settings.ProfileSchemaPath))
        {
            SchemaPath = _settings.ProfileSchemaPath;
        }

        var netDocuments = GetOrCreateNetDocumentsSettings();
        NetDocumentsRegionDefaults.EnsureDefaults(netDocuments);
        netDocuments.OAuthClientConfigRef = string.IsNullOrWhiteSpace(netDocuments.OAuthClientConfigRef)
            ? AppSettings.DefaultNetDocumentsOAuthClientProfilesRef
            : netDocuments.OAuthClientConfigRef;
        netDocuments.UseSecureOAuthClientConfig = true;
        _netDocumentsRegion = netDocuments.Region;
        OnPropertyChanged(nameof(SelectedNetDocumentsRegion));

        await LoadNetDocumentsOAuthClientProfilesAsync();
        await MigrateLegacyNetDocumentsOAuthConfigAsync(netDocuments);
        ResolveNetDocumentsOAuthClientConfig();
        _netDocumentsBootstrapRedirectUri = string.IsNullOrWhiteSpace(netDocuments.RedirectUri)
            ? NetDocumentsRegionDefaults.DefaultRedirectUri
            : netDocuments.RedirectUri;
        OnPropertyChanged(nameof(NetDocumentsConnectionProfileStatus));
        _selectedNetDocumentsRepositoryId = netDocuments.SelectedRepositoryId ?? string.Empty;
        _selectedNetDocumentsCabinetId = netDocuments.SelectedCabinetId ?? string.Empty;
        _selectedNetDocumentsCabinetName = netDocuments.SelectedCabinetName ?? string.Empty;
        RestoreTargetSelectionFromSettings(netDocuments);
        _isExportMode = netDocuments.IsExportMode;
        _exportDestinationRootPath = netDocuments.ExportDestinationRootPath ?? string.Empty;
        _exportAllVersions = netDocuments.ExportAllVersions;
        _exportMetadataFormat = netDocuments.ExportMetadataFormat;
        _exportDownloadFiltersAsFolders = netDocuments.ExportDownloadFiltersAsFolders;
        _exportIncludeCustomAttributes = netDocuments.ExportIncludeCustomAttributes;
        OnPropertyChanged(nameof(SelectedNetDocumentsRepositoryId));
        OnPropertyChanged(nameof(SelectedNetDocumentsCabinetId));
        OnPropertyChanged(nameof(SelectedNetDocumentsCabinetName));
        OnPropertyChanged(nameof(IsExportMode));
        OnPropertyChanged(nameof(IsImportMode));
        OnPropertyChanged(nameof(ShowImportContext));
        OnPropertyChanged(nameof(ShowExportContext));
        OnPropertyChanged(nameof(ShowReviewTargetProfileContext));
        OnPropertyChanged(nameof(JobOverviewTitle));
        OnPropertyChanged(nameof(ExportModeToggleText));
        OnPropertyChanged(nameof(ExportDestinationRootPath));
        OnPropertyChanged(nameof(ExportAllVersions));
        OnPropertyChanged(nameof(ExportMetadataFormat));
        OnPropertyChanged(nameof(ExportDownloadFiltersAsFolders));
        OnPropertyChanged(nameof(ExportIncludeCustomAttributes));
        SyncNdImportCabinetFromSelectedCabinetId();
        OnPropertyChanged(nameof(IsDeveloperMode));
        OnPropertyChanged(nameof(NetDocumentsBootstrapRedirectUri));
        OnPropertyChanged(nameof(CanShowNetDocumentsDevBootstrap));
        OnPropertyChanged(nameof(CanSaveNetDocumentsDevBootstrap));
        OnPropertyChanged(nameof(CanConnectToNetDocuments));

        UpdateSchemaMatch();
    }

    private void QueueSettingsSave()
    {
        lock (_settingsSaveLock)
        {
            if (_settingsSavePending)
            {
                return;
            }

            _settingsSavePending = true;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(200);
            await SaveSettingsAsync();
            lock (_settingsSaveLock)
            {
                _settingsSavePending = false;
            }
        });
    }

    private async Task SaveSettingsAsync()
    {
        var previousSecretRef = _settings.NdImportPasswordRef;
        var netDocuments = GetOrCreateNetDocumentsSettings();

        _settings.NdImportPath = NdImportPath;
        _settings.NdImportHost = NdImportHost;
        _settings.NdImportCabinet = NdImportCabinet;
        _settings.NdImportUsername = NdImportUsername;
        _settings.NdImportDateFormat = NdImportDateFormat;
        _settings.ImportExecutionMode = SelectedImportExecutionMode.ToString();
        _settings.RememberNdImportPassword = RememberNdImportPassword;
        _settings.NdImportPasswordRef = RememberNdImportPassword ? AppSettings.DefaultNdImportPasswordRef : string.Empty;
        _settings.ProfileSchemaPath = SchemaPath;
        _settings.Theme = IsDarkMode ? "Dark" : "Light";
        netDocuments.Region = SelectedNetDocumentsRegion;
        netDocuments.UseSecureOAuthClientConfig = true;
        netDocuments.OAuthClientConfigRef = string.IsNullOrWhiteSpace(netDocuments.OAuthClientConfigRef)
            ? AppSettings.DefaultNetDocumentsOAuthClientProfilesRef
            : netDocuments.OAuthClientConfigRef;
        netDocuments.ClientId = string.Empty;
        netDocuments.RedirectUri = string.Empty;
        netDocuments.ClientSecretRef = string.Empty;
        netDocuments.SelectedRepositoryId = SelectedNetDocumentsRepositoryId;
        netDocuments.SelectedCabinetId = SelectedNetDocumentsCabinetId;
        netDocuments.SelectedCabinetName = SelectedNetDocumentsCabinetName;
        netDocuments.IsExportMode = IsExportMode;
        netDocuments.ExportDestinationRootPath = ExportDestinationRootPath;
        netDocuments.ExportAllVersions = ExportAllVersions;
        netDocuments.ExportMetadataFormat = ExportMetadataFormat;
        netDocuments.ExportDownloadFiltersAsFolders = ExportDownloadFiltersAsFolders;
        netDocuments.ExportIncludeCustomAttributes = ExportIncludeCustomAttributes;
        SaveTargetSelectionToSettings(netDocuments);
        NetDocumentsRegionDefaults.EnsureDefaults(netDocuments);

        if (RememberNdImportPassword && !string.IsNullOrWhiteSpace(NdImportPassword))
        {
            await _secretStore.WriteSecretAsync(_settings.NdImportPasswordRef, NdImportPassword);
        }
        else
        {
            var secretToDelete = string.IsNullOrWhiteSpace(previousSecretRef)
                ? AppSettings.DefaultNdImportPasswordRef
                : previousSecretRef;
            _secretStore.DeleteSecret(secretToDelete);
        }

        await AppSettings.SaveAsync(_paths.SettingsPath, _settings);
    }

    private string GetPasswordSecretName()
    {
        return string.IsNullOrWhiteSpace(_settings.NdImportPasswordRef)
            ? AppSettings.DefaultNdImportPasswordRef
            : _settings.NdImportPasswordRef;
    }

    private NetDocumentsConnectionSettings GetOrCreateNetDocumentsSettings()
    {
        _settings.NetDocumentsConnection ??= new NetDocumentsConnectionSettings();
        return _settings.NetDocumentsConnection;
    }

    private static bool IsDarkTheme(string? theme)
    {
        return string.Equals(theme, "Dark", StringComparison.OrdinalIgnoreCase);
    }

    private async Task LoadNetDocumentsOAuthClientProfilesAsync(CancellationToken cancellationToken = default)
    {
        _netDocumentsOAuthClientProfiles.Clear();
        var profiles = await _netDocumentsOAuthClientConfigProvider.LoadAsync(cancellationToken);
        foreach (var profile in profiles)
        {
            _netDocumentsOAuthClientProfiles[profile.Key] = profile.Value;
        }
    }

    private async Task SaveNetDocumentsOAuthClientProfilesAsync(CancellationToken cancellationToken = default)
    {
        await _netDocumentsOAuthClientConfigProvider.SaveAsync(_netDocumentsOAuthClientProfiles, cancellationToken);
    }

    private void ResolveNetDocumentsOAuthClientConfig()
    {
        var key = SelectedNetDocumentsRegion.ToString();
        if (!_netDocumentsOAuthClientProfiles.TryGetValue(key, out var configured))
        {
            _selectedNetDocumentsOAuthClientConfig = null;
            return;
        }

        var regionDefaults = GetSelectedNetDocumentsRegionSetting();
        configured.Region = SelectedNetDocumentsRegion;
        configured.ApiBaseUrl = string.IsNullOrWhiteSpace(configured.ApiBaseUrl) ? regionDefaults.ApiBaseUrl : configured.ApiBaseUrl;
        configured.OAuthAuthorizeBaseUrl = string.IsNullOrWhiteSpace(configured.OAuthAuthorizeBaseUrl) ? regionDefaults.OAuthAuthorizeBaseUrl : configured.OAuthAuthorizeBaseUrl;
        configured.OAuthTokenUrl = string.IsNullOrWhiteSpace(configured.OAuthTokenUrl) ? regionDefaults.OAuthTokenUrl : configured.OAuthTokenUrl;
        configured.RedirectUri = string.IsNullOrWhiteSpace(configured.RedirectUri) ? NetDocumentsRegionDefaults.DefaultRedirectUri : configured.RedirectUri;
        _selectedNetDocumentsOAuthClientConfig = configured;
    }

    private async Task MigrateLegacyNetDocumentsOAuthConfigAsync(NetDocumentsConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        var regionKey = settings.Region.ToString();
        if (_netDocumentsOAuthClientProfiles.ContainsKey(regionKey))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(settings.ClientId))
        {
            return;
        }

        var secretName = string.IsNullOrWhiteSpace(settings.ClientSecretRef)
            ? AppSettings.DefaultNetDocumentsClientSecretRef
            : settings.ClientSecretRef;
        var legacySecret = await _secretStore.ReadSecretAsync(secretName, cancellationToken) ?? string.Empty;
        var regionDefaults = GetSelectedNetDocumentsRegionSetting();
        var migrated = new NetDocumentsOAuthClientConfig
        {
            Region = settings.Region,
            ClientId = settings.ClientId.Trim(),
            ClientSecret = legacySecret,
            RedirectUri = string.IsNullOrWhiteSpace(settings.RedirectUri)
                ? NetDocumentsRegionDefaults.DefaultRedirectUri
                : settings.RedirectUri.Trim(),
            ApiBaseUrl = regionDefaults.ApiBaseUrl,
            OAuthAuthorizeBaseUrl = regionDefaults.OAuthAuthorizeBaseUrl,
            OAuthTokenUrl = regionDefaults.OAuthTokenUrl
        };

        _netDocumentsOAuthClientProfiles[regionKey] = migrated;
        await SaveNetDocumentsOAuthClientProfilesAsync(cancellationToken);

        settings.ClientId = string.Empty;
        settings.ClientSecretRef = string.Empty;
        settings.RedirectUri = string.Empty;
        settings.UseSecureOAuthClientConfig = true;
        settings.OAuthClientConfigRef = AppSettings.DefaultNetDocumentsOAuthClientProfilesRef;
        _secretStore.DeleteSecret(secretName);
        await AppSettings.SaveAsync(_paths.SettingsPath, _settings, cancellationToken);
    }

    private NetDocumentsRegionSetting GetSelectedNetDocumentsRegionSetting()
    {
        var settings = GetOrCreateNetDocumentsSettings();
        NetDocumentsRegionDefaults.EnsureDefaults(settings);
        var key = SelectedNetDocumentsRegion.ToString();
        if (settings.Regions.TryGetValue(key, out var configured) && configured is not null)
        {
            return configured;
        }

        var fallback = NetDocumentsRegionDefaults.GetDefaults(SelectedNetDocumentsRegion);
        settings.Regions[key] = fallback;
        return fallback;
    }

    private void UpdateSchemaMatch()
    {
        if (_schema is null)
        {
            SchemaCabinetMatches = false;
            return;
        }

        var activeCabinet = string.IsNullOrWhiteSpace(SelectedNetDocumentsCabinetName)
            ? NdImportCabinet
            : SelectedNetDocumentsCabinetName;

        if (string.IsNullOrWhiteSpace(activeCabinet))
        {
            SchemaCabinetMatches = true;
            return;
        }

        if (string.IsNullOrWhiteSpace(_schema.CabinetName))
        {
            SchemaCabinetMatches = true;
            return;
        }

        SchemaCabinetMatches = string.Equals(_schema.CabinetName, activeCabinet, StringComparison.OrdinalIgnoreCase);
    }

    private void ResolveProfileFieldHints()
    {
        foreach (var field in ProfileFields)
        {
            UpdateProfileFieldResolution(field);
        }
    }

    public Task LaunchNdImportAsync()
    {
        if (string.IsNullOrWhiteSpace(NdImportPath) || !File.Exists(NdImportPath))
        {
            StatusText = "Select the path to ndimport.exe before launching.";
            return Task.CompletedTask;
        }

        if (string.IsNullOrWhiteSpace(_lastNdImportExportPath) || !File.Exists(_lastNdImportExportPath))
        {
            StatusText = "Export an ndImport CSV before launching.";
            return Task.CompletedTask;
        }

        if (!ValidateNdImportInputCsv(_lastNdImportExportPath, out var validationError))
        {
            StatusText = validationError;
            return Task.CompletedTask;
        }

        if (!NdImportIncludePassword || string.IsNullOrWhiteSpace(NdImportPassword))
        {
            StatusText = "ndImport CLI requires /pass (or certificate auth). Enter password and enable 'Include password on CLI (sensitive)'.";
            return Task.CompletedTask;
        }

        var launchParameters = ResolveNdImportLaunchParameters();
        var cabinetArgument = BuildNdImportCabinetArgument(
            launchParameters.CabinetName,
            launchParameters.RepositoryName,
            launchParameters.Cabinet);
        var arguments = new List<string>
        {
            $"/host=\"{launchParameters.Host}\"",
            "/simpleHost=Y",
            cabinetArgument,
            $"/user=\"{launchParameters.Username}\"",
            $"/list=\"{launchParameters.InputCsvPath}\"",
            "/cert=N",
            $"/pass=\"{launchParameters.Password}\"",
            $"/dateformat={launchParameters.DateFormat}"
        };

        if (NdImportUtf8)
        {
            arguments.Add("/utf8=Y");
        }

        if (NdImportNoValidation)
        {
            arguments.Add("/validate=Y");
        }

        if (NdImportMaxErrors > 0)
        {
            arguments.Add($"/maxerr={NdImportMaxErrors}");
        }

        var displayArguments = arguments
            .Select(argument => argument.StartsWith("/pass=", StringComparison.OrdinalIgnoreCase) ? "/pass=***" : argument)
            .ToArray();
        var displayCliArguments = string.Join(" ", displayArguments);
        var runInTerminal = new TaskDialogButton("Run in terminal");
        var cancelLaunch = TaskDialogButton.Cancel;
        var launchDialog = new TaskDialogPage
        {
            Caption = "Launch ndImport",
            Heading = "Run ndImport in a terminal window?",
            Text = $"Command:{Environment.NewLine}\"{NdImportPath}\" {displayCliArguments}",
            Buttons = { runInTerminal, cancelLaunch }
        };

        var launchChoice = ShowTaskDialog(launchDialog);
        if (launchChoice != runInTerminal)
        {
            StatusText = "ndImport launch canceled.";
            return Task.CompletedTask;
        }

        try
        {
            CleanupStaleNdImportLaunchScripts();
            var wrapperPath = CreateNdImportLaunchScript(
                NdImportPath,
                launchParameters.Host,
                launchParameters.CabinetName,
                launchParameters.RepositoryName,
                launchParameters.Cabinet,
                launchParameters.Username,
                launchParameters.InputCsvPath,
                launchParameters.Password,
                launchParameters.DateFormat,
                NdImportUtf8,
                NdImportNoValidation,
                NdImportMaxErrors);

            System.Diagnostics.Trace.WriteLine(
                $"ND-IMPORT launch host='{launchParameters.Host}' simpleHost=Y cabinetArg='{cabinetArgument}' cabinetName='{launchParameters.CabinetName}' repositoryName='{launchParameters.RepositoryName}' cabinetFromMetadata={(launchParameters.CabinetFromMetadata ? "Y" : "N")} user='{launchParameters.Username}' list='{launchParameters.InputCsvPath}' dateformat={launchParameters.DateFormat} utf8={(NdImportUtf8 ? "Y" : "N")} validate={(NdImportNoValidation ? "Y" : "N")} maxerr={NdImportMaxErrors}");

            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/k \"\"{wrapperPath}\"\"",
                WorkingDirectory = Path.GetDirectoryName(NdImportPath),
                UseShellExecute = true
            });

            NdImportSessions.Insert(0, new NdImportSessionView(DateTime.Now, "Launched", Path.GetFileName(_lastNdImportExportPath)));
            StatusText = "ndImport launched in terminal window.";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to launch ndImport: {ex.Message}";
        }

        return Task.CompletedTask;
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

    private void OpenFile(string path)
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

    private static string NormalizeNdImportPath(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return string.Empty;
        }

        var trimmed = rawPath.Trim();
        if (trimmed.Length >= 2 &&
            trimmed.StartsWith("\"", StringComparison.Ordinal) &&
            trimmed.EndsWith("\"", StringComparison.Ordinal))
        {
            trimmed = trimmed[1..^1].Trim();
        }

        return trimmed;
    }

    private NdImportLaunchParameters ResolveNdImportLaunchParameters()
    {
        var cabinet = ResolveNdImportCabinetLaunchInfo();
        return new NdImportLaunchParameters(
            Host: NdImportHost.Trim(),
            Cabinet: cabinet.Argument,
            CabinetName: cabinet.CabinetName,
            RepositoryName: cabinet.RepositoryName,
            CabinetFromMetadata: cabinet.FromMetadata,
            Username: NdImportUsername.Trim(),
            InputCsvPath: _lastNdImportExportPath?.Trim() ?? string.Empty,
            Password: NdImportPassword,
            DateFormat: NormalizeNdImportDateFormat(NdImportDateFormat));
    }

    private NdImportCabinetLaunchInfo ResolveNdImportCabinetLaunchInfo()
    {
        var cabinetValue = NdImportCabinet.Trim();
        if (string.IsNullOrWhiteSpace(cabinetValue))
        {
            return new NdImportCabinetLaunchInfo(string.Empty, string.Empty, string.Empty, false);
        }

        var cabinet = _netDocumentsCabinets.FirstOrDefault(c =>
            string.Equals(c.CabinetId, cabinetValue, StringComparison.OrdinalIgnoreCase));

        var cabinetName = cabinet?.CabinetName ?? string.Empty;
        if (string.IsNullOrWhiteSpace(cabinetName) &&
            !string.IsNullOrWhiteSpace(_selectedNetDocumentsCabinetName) &&
            string.Equals(_selectedNetDocumentsCabinetId, cabinetValue, StringComparison.OrdinalIgnoreCase))
        {
            cabinetName = _selectedNetDocumentsCabinetName;
        }

        var repositoryId = cabinet?.RepositoryId;
        if (string.IsNullOrWhiteSpace(repositoryId))
        {
            repositoryId = _selectedNetDocumentsRepositoryId;
        }

        var repositoryName = string.Empty;
        if (!string.IsNullOrWhiteSpace(repositoryId))
        {
            repositoryName = _netDocumentsRepositories
                .FirstOrDefault(r => string.Equals(r.RepositoryId, repositoryId, StringComparison.OrdinalIgnoreCase))
                ?.RepositoryName ?? string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(cabinetName) && !string.IsNullOrWhiteSpace(repositoryName))
        {
            return new NdImportCabinetLaunchInfo($"{cabinetName} {repositoryName}", cabinetName, repositoryName, true);
        }

        if (!string.IsNullOrWhiteSpace(cabinetName))
        {
            return new NdImportCabinetLaunchInfo(cabinetName, cabinetName, repositoryName, true);
        }

        return new NdImportCabinetLaunchInfo(cabinetValue, string.Empty, string.Empty, false);
    }

    private static string NormalizeNdImportDateFormat(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "DMY";
        }

        return value.Trim().ToUpperInvariant() switch
        {
            "MDY" => "MDY",
            "YMD" => "YMD",
            _ => "DMY"
        };
    }

    private sealed record NdImportLaunchParameters(
        string Host,
        string Cabinet,
        string CabinetName,
        string RepositoryName,
        bool CabinetFromMetadata,
        string Username,
        string InputCsvPath,
        string Password,
        string DateFormat);

    private sealed record NdImportCabinetLaunchInfo(
        string Argument,
        string CabinetName,
        string RepositoryName,
        bool FromMetadata);

    private static string CreateNdImportLaunchScript(
        string ndImportPath,
        string host,
        string cabinetName,
        string repositoryName,
        string cabinetFallback,
        string username,
        string inputCsvPath,
        string password,
        string dateFormat,
        bool utf8,
        bool skipValidation,
        int maxErrors)
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "NetDocsImporter");
        Directory.CreateDirectory(tempDirectory);

        var scriptPath = Path.Combine(tempDirectory, $"launch-ndimport-{DateTime.UtcNow:yyyyMMddHHmmssfff}.cmd");

        var cabinetArgument = BuildNdImportCabinetArgument(cabinetName, repositoryName, cabinetFallback);
        var arguments = new List<string>
        {
            BuildNdImportArg("host", host),
            "/simpleHost=Y",
            cabinetArgument,
            BuildNdImportArg("user", username),
            BuildNdImportArg("list", inputCsvPath),
            BuildNdImportArg("pass", password),
            "/cert=N",
            $"/dateformat={NormalizeNdImportDateFormat(dateFormat)}"
        };

        if (utf8)
        {
            arguments.Add("/utf8=Y");
        }

        if (skipValidation)
        {
            arguments.Add("/validate=Y");
        }

        if (maxErrors > 0)
        {
            arguments.Add($"/maxerr={maxErrors}");
        }

        var script = new StringBuilder();
        script.AppendLine("@echo off");
        script.AppendLine("setlocal DisableDelayedExpansion");
        script.AppendLine($"\"{EscapeBatchValue(ndImportPath)}\" {string.Join(" ", arguments)}");
        script.AppendLine("set \"NDIMPORT_EXITCODE=%ERRORLEVEL%\"");
        script.AppendLine("echo.");
        script.AppendLine("echo ndImport exited with code %NDIMPORT_EXITCODE%.");
        script.AppendLine("echo.");

        File.WriteAllText(scriptPath, script.ToString(), new UTF8Encoding(false));
        return scriptPath;
    }

    private static void CleanupStaleNdImportLaunchScripts()
    {
        try
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), "NetDocsImporter");
            if (!Directory.Exists(tempDirectory))
            {
                return;
            }

            var cutoffUtc = DateTime.UtcNow.AddDays(-2);
            foreach (var file in Directory.EnumerateFiles(tempDirectory, "launch-ndimport-*.cmd"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoffUtc)
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                    // Best-effort cleanup only.
                }
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private static string BuildNdImportArg(string name, string value)
    {
        return $"/{name}=\"{EscapeBatchValue(value)}\"";
    }

    private static string BuildNdImportCabinetArgument(string cabinetName, string repositoryName, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(cabinetName) && !string.IsNullOrWhiteSpace(repositoryName))
        {
            return $"/cab=\"{EscapeBatchValue(cabinetName)}  ({EscapeBatchValue(repositoryName)})\"";
        }

        var effective = string.IsNullOrWhiteSpace(cabinetName) ? fallback : cabinetName;
        return $"/cab=\"{EscapeBatchValue(effective)}\"";
    }

    private static string EscapeBatchValue(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value
            .Replace("%", "%%", StringComparison.Ordinal)
            .Replace("\"", "\"\"", StringComparison.Ordinal);
    }

    private static TaskDialogButton ShowTaskDialog(TaskDialogPage page)
    {
        try
        {
            var mainWindow = System.Windows.Application.Current?.MainWindow;
            if (mainWindow is not null)
            {
                var handle = new System.Windows.Interop.WindowInteropHelper(mainWindow).Handle;
                if (handle != IntPtr.Zero)
                {
                    return TaskDialog.ShowDialog(new OwnedWin32Window(handle), page);
                }
            }
        }
        catch
        {
            // Fall back to unowned dialog when owner lookup fails.
        }

        return TaskDialog.ShowDialog(page);
    }

    private sealed class OwnedWin32Window : System.Windows.Forms.IWin32Window
    {
        public OwnedWin32Window(IntPtr handle)
        {
            Handle = handle;
        }

        public IntPtr Handle { get; }
    }

    private static bool ValidateNdImportInputCsv(string csvPath, out string error)
    {
        error = string.Empty;

        if (Path.GetFileName(csvPath).Contains("-warnings", StringComparison.OrdinalIgnoreCase))
        {
            error = "Selected file is a diagnostics report. Launch ndImport with the Export CSV file, not a -warnings.csv file.";
            return false;
        }

        string? header;
        try
        {
            using var reader = new StreamReader(csvPath);
            header = reader.ReadLine();
        }
        catch (Exception ex)
        {
            error = $"Unable to read export CSV header: {ex.Message}";
            return false;
        }

        if (string.IsNullOrWhiteSpace(header))
        {
            error = "Export CSV is empty. Re-export before launching ndImport.";
            return false;
        }

        var columns = header
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var requiredColumns = new[] { "FULL PATH", "DOCUMENT NAME", "DOCUMENT EXTENSION", "FOLDER" };
        var missingColumn = requiredColumns.FirstOrDefault(required => !columns.Contains(required));
        if (missingColumn is not null)
        {
            error = $"Export CSV is missing required ndImport column '{missingColumn}'. Re-export and use the main ndImport CSV file.";
            return false;
        }

        return true;
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

    public async Task RefreshPreExportWarningsAsync()
    {
        if (string.IsNullOrWhiteSpace(CurrentJobId))
        {
            UpdateOnUi(() =>
            {
                PreExportWarnings.Clear();
                _preExportLargeFileWarnings = 0;
                _preExportEmptyFolderWarnings = 0;
                PreExportWarningsStatus = "Select a folder to run checks.";
                OnPropertyChanged(nameof(PreExportLargeFileWarningsDisplay));
                OnPropertyChanged(nameof(PreExportEmptyFolderWarningsDisplay));
            });
            return;
        }

        IsPreExportWarningsBusy = true;
        try
        {
            var exporter = new NdImportCsvExporter(_jobStore);
            var preview = await exporter.PreviewWarningsAsync(CurrentJobId);

            UpdateOnUi(() =>
            {
                PreExportWarnings.Clear();
                foreach (var warning in preview.Warnings)
                {
                    var type = warning.Type switch
                    {
                        "LARGE_FILE" => "Large file",
                        "EMPTY_FOLDER" => "Empty folder",
                        _ => warning.Type
                    };
                    PreExportWarnings.Add(new PreExportWarningView(type, warning.Path, warning.Detail));
                }

                _preExportLargeFileWarnings = preview.LargeFileWarnings;
                _preExportEmptyFolderWarnings = preview.EmptyFolderWarnings;
                PreExportWarningsStatus = PreExportWarnings.Count == 0
                    ? "No large-file or empty-folder issues detected."
                    : "Review these issues before exporting for ndImport.";

                OnPropertyChanged(nameof(PreExportLargeFileWarningsDisplay));
                OnPropertyChanged(nameof(PreExportEmptyFolderWarningsDisplay));
            });
        }
        catch (Exception ex)
        {
            UpdateOnUi(() =>
            {
                PreExportWarningsStatus = $"Unable to run pre-export checks: {ex.Message}";
            });
        }
        finally
        {
            IsPreExportWarningsBusy = false;
        }
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
        var folderEffectiveIncluded = _selectedFolderNode.EffectiveIncluded;
        var filter = SelectedFileFilter;
        var search = FileSearchText?.Trim();

        var filtered = files
            .Where(f => string.IsNullOrWhiteSpace(search) || f.FullPath.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Select(f => new
            {
                Record = f,
                EffectiveIncluded = ResolveFileEffectiveIncluded(f, folderEffectiveIncluded)
            })
            .Where(f => filter switch
            {
                "Included" => f.EffectiveIncluded,
                "Excluded" => !f.EffectiveIncluded,
                "Overrides" => !string.Equals(f.Record.ImportMode, "inherit", StringComparison.OrdinalIgnoreCase),
                "Large" => f.Record.IsLargeWarning,
                _ => true
            })
            .Select(f => new FileRowView(f.Record, f.EffectiveIncluded))
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

    private static bool ResolveFileEffectiveIncluded(FileRecord record, bool folderEffectiveIncluded)
    {
        return record.ImportMode switch
        {
            "include" => true,
            "exclude" => false,
            _ => folderEffectiveIncluded
        };
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
        await RefreshFolderImportCountsAsync();
        await RefreshPreExportWarningsAsync();
    }

    public async Task ApplyImportModeToChildrenAsync()
    {
        if (_selectedFolderNode is null)
        {
            return;
        }

        await _jobStore.ApplyImportModeToDescendantsAsync(_selectedFolderNode.JobId, _selectedFolderNode.FolderId, _selectedFolderNode.ImportMode);
        await RefreshSelectedFolderSummaryAsync();
        await RefreshFolderImportCountsAsync();
        await RefreshPreExportWarningsAsync();
    }

    public async Task SetFileImportModeAsync(IReadOnlyList<FileRowView> rows, string importMode)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var reason = importMode switch
        {
            "include" => "User included",
            "exclude" => "User excluded",
            _ => null
        };

        var fileIds = rows.Select(r => r.FileId).ToList();
        await _jobStore.UpdateFileImportModeAsync(fileIds, importMode, reason);

        await RefreshFolderFilesAsync();
        await RefreshSelectedFolderSummaryAsync();
        await RefreshFolderImportCountsAsync();
        await RefreshPreExportWarningsAsync();
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
        await RefreshReviewScopeNetDocumentsAsync();
    }

    public void AddProfileField()
    {
        ProfileFields.Add(new ProfileFieldView(string.Empty, string.Empty, ProfileFieldMode.Label));
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
        await RefreshReviewScopeNetDocumentsAsync();
    }

    private void LoadProfileFields(string? payloadJson)
    {
        ProfileFields.CollectionChanged -= OnProfileFieldsChanged;
        ProfileFields.Clear();

        var entries = ProfilePayloadCodec.Deserialize(payloadJson);
        foreach (var entry in entries)
        {
            var field = new ProfileFieldView(entry.Field, entry.Value, entry.Mode);
            field.PropertyChanged += OnProfileFieldPropertyChanged;
            ProfileFields.Add(field);
            UpdateProfileFieldResolution(field);
        }

        ProfileFields.CollectionChanged += OnProfileFieldsChanged;
    }

    private string? SerializeProfileFields()
    {
        var entries = ProfileFields
            .Where(field => !string.IsNullOrWhiteSpace(field.Key))
            .Select(field => new ProfileFieldEntry(field.Key, field.Value ?? string.Empty, field.Mode))
            .ToList();

        return ProfilePayloadCodec.Serialize(entries);
    }

    private void OnProfileFieldsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (ProfileFieldView field in e.NewItems)
            {
                field.PropertyChanged += OnProfileFieldPropertyChanged;
                UpdateProfileFieldResolution(field);
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
        if (sender is ProfileFieldView field)
        {
            UpdateProfileFieldResolution(field);
        }

        QueueProfileSave();
    }

    private void UpdateProfileFieldResolution(ProfileFieldView field)
    {
        if (_schema is null)
        {
            field.ClearResolution();
            return;
        }

        if (field.Mode == ProfileFieldMode.Code)
        {
            if (_schema.TryResolveFieldName(field.Key, out var name))
            {
                field.ResolvedFieldLabel = name;
                field.HasFieldWarning = false;
            }
            else
            {
                field.ResolvedFieldLabel = string.Empty;
                field.HasFieldWarning = true;
            }

            if (_schema.TryResolveValueLabel(field.Key, field.Value, out var label))
            {
                field.ResolvedValueLabel = label;
                field.HasValueWarning = false;
            }
            else
            {
                field.ResolvedValueLabel = string.Empty;
                field.HasValueWarning = !string.IsNullOrWhiteSpace(field.Value);
            }
        }
        else
        {
            if (_schema.TryResolveFieldCode(field.Key, out _))
            {
                field.HasFieldWarning = false;
            }
            else
            {
                field.HasFieldWarning = !string.IsNullOrWhiteSpace(field.Key);
            }

            if (_schema.TryResolveValueCode(field.Key, field.Value, out _))
            {
                field.HasValueWarning = false;
            }
            else
            {
                field.HasValueWarning = !string.IsNullOrWhiteSpace(field.Value);
            }

            field.ResolvedFieldLabel = string.Empty;
            field.ResolvedValueLabel = string.Empty;
        }
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
        await RefreshReviewScopeNetDocumentsAsync();
    }

    private void SetCurrentStep(StepKey key)
    {
        var step = Steps.FirstOrDefault(item => item.Key == key);
        if (step is not null)
        {
            CurrentStep = step;
        }
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
        if (_uiContext is null || ReferenceEquals(SynchronizationContext.Current, _uiContext))
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
        LastRunDisplay = "--";
        LastRunStatus = "--";
        LastRunSummary = "--";
    }

    public string JobId { get; }

    public string JobIdShort { get; }

    public string CreatedDisplay { get; }

    public string SourceRoot { get; }

    public string Status { get; }

    public string FileCountDisplay { get; }

    public string TotalBytesDisplay { get; }

    public string LargeWarningsDisplay { get; }

    public string LastRunDisplay { get; private set; }

    public string LastRunStatus { get; private set; }

    public string LastRunSummary { get; private set; }

    public void ApplyLatestRun(CompletedJobRunSummary summary)
    {
        LastRunDisplay = summary.StartedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        LastRunStatus = string.IsNullOrWhiteSpace(summary.Status) ? "--" : summary.Status;
        LastRunSummary = string.IsNullOrWhiteSpace(summary.Summary) ? "--" : summary.Summary;
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

public sealed class NdImportSessionView
{
    public NdImportSessionView(DateTime started, string status, string details)
    {
        StartedDisplay = started.ToString("g", CultureInfo.CurrentCulture);
        StatusDisplay = status;
        Details = details;
    }

    public string StartedDisplay { get; }

    public string StatusDisplay { get; }

    public string Details { get; }
}

public sealed class ProfileFieldView : INotifyPropertyChanged
{
    private string _key;
    private string _value;
    private ProfileFieldMode _mode;
    private string _resolvedFieldLabel = string.Empty;
    private string _resolvedValueLabel = string.Empty;
    private bool _hasFieldWarning;
    private bool _hasValueWarning;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ProfileFieldView(string key, string value, ProfileFieldMode mode)
    {
        _key = key;
        _value = value;
        _mode = mode;
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

    public ProfileFieldMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value)
            {
                return;
            }

            _mode = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Mode)));
        }
    }

    public string ResolvedFieldLabel
    {
        get => _resolvedFieldLabel;
        set
        {
            if (_resolvedFieldLabel == value)
            {
                return;
            }

            _resolvedFieldLabel = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ResolvedFieldLabel)));
        }
    }

    public string ResolvedValueLabel
    {
        get => _resolvedValueLabel;
        set
        {
            if (_resolvedValueLabel == value)
            {
                return;
            }

            _resolvedValueLabel = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ResolvedValueLabel)));
        }
    }

    public bool HasFieldWarning
    {
        get => _hasFieldWarning;
        set
        {
            if (_hasFieldWarning == value)
            {
                return;
            }

            _hasFieldWarning = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasFieldWarning)));
        }
    }

    public bool HasValueWarning
    {
        get => _hasValueWarning;
        set
        {
            if (_hasValueWarning == value)
            {
                return;
            }

            _hasValueWarning = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasValueWarning)));
        }
    }

    public void ClearResolution()
    {
        ResolvedFieldLabel = string.Empty;
        ResolvedValueLabel = string.Empty;
        HasFieldWarning = false;
        HasValueWarning = false;
    }
}

public sealed class FileRowView
{
    public FileRowView(FileRecord record, bool effectiveIncluded)
    {
        FileId = record.FileId;
        Name = Path.GetFileName(record.FullPath);
        RelativePath = record.RelativePath;
        SizeDisplay = FormatBytes(record.SizeBytes);
        ModifiedDisplay = record.ModifiedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        LargeWarning = record.IsLargeWarning ? "Yes" : "No";
        EffectiveImport = effectiveIncluded ? "Yes" : "No";
        OverrideMode = record.ImportMode;
        ImportReason = record.ImportReason ?? string.Empty;
    }

    public string FileId { get; }

    public string Name { get; }

    public string RelativePath { get; }

    public string SizeDisplay { get; }

    public string ModifiedDisplay { get; }

    public string LargeWarning { get; }

    public string EffectiveImport { get; }

    public string OverrideMode { get; }

    public string ImportReason { get; }

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

public sealed class PreExportWarningView
{
    public PreExportWarningView(string type, string path, string detail)
    {
        Type = type;
        Path = path;
        Detail = detail;
    }

    public string Type { get; }

    public string Path { get; }

    public string Detail { get; }
}

public enum StepKey
{
    SelectFolder,
    ReviewScope,
    Profiling,
    NdImportConfig,
    RunImport,
    RecentJobs
}

public sealed class StepItem
{
    public StepItem(int number, StepKey key, string title, string subtitle, object viewModel)
    {
        Number = number;
        Key = key;
        Title = title;
        Subtitle = subtitle;
        ViewModel = viewModel;
        IsEnabled = true;
    }

    public int Number { get; }

    public StepKey Key { get; }

    public string Title { get; }

    public string Subtitle { get; }

    public object ViewModel { get; }

    public bool IsEnabled { get; set; }
}
