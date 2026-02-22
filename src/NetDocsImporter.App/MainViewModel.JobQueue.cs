using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using NetDocsImporter.Core;
using NetDocsImporter.Data;

namespace NetDocsImporter.App;

public sealed partial class MainViewModel
{
    private readonly ObservableCollection<UploadQueueJobView> _uploadQueueJobs = new();
    private UploadJobMonitor? _uploadJobMonitor;
    private CancellationTokenSource? _uploadJobMonitorCancellation;
    private string _queueMonitorStatus = "Queue monitor idle.";
    private string _queueStartupNotice = string.Empty;
    private string _quickViewRunningJobText = "Running: none";
    private string _quickViewNextJob1Text = "Next: --";
    private string _quickViewNextJob2Text = "--";
    private string _quickViewNextJob3Text = "--";
    private int _queueRefreshInFlight;

    public ObservableCollection<UploadQueueJobView> UploadQueueJobs => _uploadQueueJobs;

    public bool CanAddDirectUploadToQueue => CanRunDirectUpload;

    public bool CanScheduleDirectUpload => CanRunDirectUpload;

    public string QueueMonitorStatus
    {
        get => _queueMonitorStatus;
        private set => SetField(ref _queueMonitorStatus, value);
    }

    public string QueueStartupNotice
    {
        get => _queueStartupNotice;
        private set
        {
            if (SetField(ref _queueStartupNotice, value))
            {
                OnPropertyChanged(nameof(HasQueueStartupNotice));
            }
        }
    }

    public bool HasQueueStartupNotice => !string.IsNullOrWhiteSpace(QueueStartupNotice);

    public string QuickViewRunningJobText
    {
        get => _quickViewRunningJobText;
        private set => SetField(ref _quickViewRunningJobText, value);
    }

    public string QuickViewNextJob1Text
    {
        get => _quickViewNextJob1Text;
        private set => SetField(ref _quickViewNextJob1Text, value);
    }

    public string QuickViewNextJob2Text
    {
        get => _quickViewNextJob2Text;
        private set => SetField(ref _quickViewNextJob2Text, value);
    }

    public string QuickViewNextJob3Text
    {
        get => _quickViewNextJob3Text;
        private set => SetField(ref _quickViewNextJob3Text, value);
    }

    public async Task AddCurrentDirectUploadToQueueAsync()
    {
        await EnqueueCurrentDirectUploadAsync(null);
    }

    public async Task ScheduleCurrentDirectUploadAsync(DateTime scheduledForLocal)
    {
        await EnqueueCurrentDirectUploadAsync(scheduledForLocal.ToUniversalTime());
    }

    public async Task StartUploadQueueMonitorAsync()
    {
        if (_uploadJobMonitor is not null)
        {
            return;
        }

        await _jobStore.InitializeAsync();
        _uploadJobMonitorCancellation = new CancellationTokenSource();
        _uploadJobMonitor = new UploadJobMonitor(
            _jobStore,
            new MainViewModelUploadRunner(this),
            new SystemClock(),
            TimeSpan.FromSeconds(5));
        await _uploadJobMonitor.StartAsync(_uploadJobMonitorCancellation.Token);
        QueueMonitorStatus = "Queue monitor running.";

        if (_uploadJobMonitor.StartupPromotedDueCount > 0)
        {
            QueueStartupNotice =
                $"{_uploadJobMonitor.StartupPromotedDueCount:N0} queued jobs were due or interrupted while the app was closed.";
        }

        await LoadQueueJobsAsync();
    }

    public void StopUploadQueueMonitor()
    {
        _uploadJobMonitorCancellation?.Cancel();
        _uploadJobMonitorCancellation?.Dispose();
        _uploadJobMonitorCancellation = null;

        if (_uploadJobMonitor is not null)
        {
            _ = _uploadJobMonitor.DisposeAsync();
            _uploadJobMonitor = null;
        }

        QueueMonitorStatus = "Queue monitor stopped.";
    }

    public async Task LoadQueueJobsAsync()
    {
        if (Interlocked.Exchange(ref _queueRefreshInFlight, 1) == 1)
        {
            return;
        }

        try
        {
            await _jobStore.InitializeAsync();
            var queued = await _jobStore.GetQueueViewAsync(200);
            var running = await _jobStore.GetRunningJobAsync();

            UpdateOnUi(() =>
            {
                _uploadQueueJobs.Clear();
                foreach (var item in queued)
                {
                    _uploadQueueJobs.Add(new UploadQueueJobView(item));
                }

                QuickViewRunningJobText = running is null
                    ? "Running: none"
                    : $"Running: {FormatQuickJobTitle(running)}";

                QuickViewNextJob1Text = queued.Count >= 1 ? $"Next: {FormatQuickJobTitle(queued[0])}" : "Next: --";
                QuickViewNextJob2Text = queued.Count >= 2 ? FormatQuickJobTitle(queued[1]) : "--";
                QuickViewNextJob3Text = queued.Count >= 3 ? FormatQuickJobTitle(queued[2]) : "--";
            });
        }
        finally
        {
            Interlocked.Exchange(ref _queueRefreshInFlight, 0);
        }
    }

    public void OpenJobsStep()
    {
        SetCurrentStep(StepKey.RecentJobs);
    }

    public void ClearQueueStartupNotice()
    {
        QueueStartupNotice = string.Empty;
    }

    private async Task EnqueueCurrentDirectUploadAsync(DateTime? scheduledForUtc)
    {
        var snapshot = await TryCreateQueueSnapshotAsync();
        if (snapshot is null)
        {
            return;
        }

        await _jobStore.InitializeAsync();
        var created = await _jobStore.CreateUploadQueueJobAsync(
            snapshot.SourceJobId,
            snapshot.SourceRoot,
            snapshot.ToJson(),
            DateTime.UtcNow,
            scheduledForUtc);

        StatusText = scheduledForUtc.HasValue
            ? $"Scheduled upload job {created.QueueJobId[..8]} for {scheduledForUtc.Value.ToLocalTime():g}."
            : $"Added upload job {created.QueueJobId[..8]} to queue.";
        await LoadQueueJobsAsync();
    }

    private async Task<UploadQueueSnapshot?> TryCreateQueueSnapshotAsync()
    {
        if (!IsDirectApiMode)
        {
            StatusText = "Switch to DirectApi mode to queue uploads.";
            return null;
        }

        if (string.IsNullOrWhiteSpace(CurrentJobId))
        {
            StatusText = "Select and scan a source folder before queuing uploads.";
            return null;
        }

        if (_selectedNetDocumentsTarget is null || !_selectedNetDocumentsTargetSupported || !CanConfirmNetDocumentsTarget)
        {
            StatusText = "Confirm a NetDocuments target before queuing uploads.";
            return null;
        }

        if (_directUploadPlan is null ||
            _directUploadPlan.Files.Count == 0 ||
            !IsDirectUploadPlanAlignedWithCurrentContext())
        {
            await RefreshDirectUploadPreflightAsync();
        }

        if (_directUploadPlan is null || !_directUploadPlan.CanUpload || _directUploadPlan.Files.Count == 0)
        {
            StatusText = "Direct upload preflight is not ready. Resolve issues before queuing.";
            return null;
        }

        var context = BuildDirectUploadPlanContext(allowCreateFolders: true);
        return new UploadQueueSnapshot
        {
            SourceJobId = CurrentJobId ?? string.Empty,
            SourceRoot = CurrentJobSourceRoot ?? string.Empty,
            RepositoryId = SelectedNetDocumentsRepositoryId ?? string.Empty,
            CabinetId = SelectedNetDocumentsCabinetId ?? string.Empty,
            TargetDisplayName = SelectedNetDocumentsTargetName ?? string.Empty,
            Target = CloneSelection(_selectedNetDocumentsTarget),
            PlanContext = context,
            CapturedUtc = DateTime.UtcNow
        };
    }

    private async Task<UploadRunnerResult> RunQueuedUploadJobAsync(
        UploadQueueJobRecord queuedJob,
        CancellationToken cancellationToken)
    {
        var snapshot = UploadQueueSnapshot.FromJson(queuedJob.SnapshotJson);
        if (snapshot is null)
        {
            return new UploadRunnerResult(false, "Job snapshot is invalid.");
        }

        if (string.IsNullOrWhiteSpace(snapshot.SourceJobId))
        {
            return new UploadRunnerResult(false, "Job snapshot missing source job id.");
        }

        if (!UploadQueueContextValidator.TryValidate(
                snapshot,
                SelectedNetDocumentsRepositoryId ?? string.Empty,
                SelectedNetDocumentsCabinetId ?? string.Empty,
                GetApiBaseUrl(),
                out var contextError))
        {
            return new UploadRunnerResult(false, contextError);
        }

        var runStartedUtc = DateTime.UtcNow;
        try
        {
            IsDirectUploadBusy = true;
            DirectUploadProgressPercent = 0;
            OnPropertyChanged(nameof(DirectUploadProgressPercentDisplay));
            StatusText = $"Queue running upload job {queuedJob.QueueJobId[..8]}...";
            DirectUploadStatus = $"Queued upload started for {snapshot.TargetDisplayName}.";

            var service = RequireDirectUploadService();
            var plan = await service.BuildPlanAsync(snapshot.SourceJobId, snapshot.Target, snapshot.PlanContext, cancellationToken);
            if (!plan.CanUpload || plan.Files.Count == 0)
            {
                return new UploadRunnerResult(false, "Execution plan blocked or empty at runtime.");
            }

            var result = await service.UploadAsync(plan, snapshot.PlanContext, null, cancellationToken);
            var reportPath = await WriteDirectUploadReportAsync(plan, result, runStartedUtc);
            var runLogPath = await WriteDirectUploadRunLogAsync(plan, result, reportPath, runStartedUtc);

            _lastDirectUploadReportPath = reportPath;
            _lastDirectUploadLogPath = runLogPath;
            OnPropertyChanged(nameof(CanOpenLastDirectUploadReport));
            OnPropertyChanged(nameof(CanExportDirectUploadLog));
            DirectUploadProgressPercent = 100;
            OnPropertyChanged(nameof(DirectUploadProgressPercentDisplay));

            var queueSucceeded = result.FailedFiles == 0;
            var runStatus = queueSucceeded ? "DirectUpload" : "DirectUpload Partial";
            var runSummaryText =
                $"Queue uploaded {result.SucceededFiles:N0}/{result.TotalRequestedFiles:N0} (Skipped {result.SkippedFiles:N0}, Resumed {result.ResumedFiles:N0}), Created {result.CreatedFolders:N0}, Failed {result.FailedFiles:N0}, report {Path.GetFileName(reportPath)}";

            await _completedJobLogStore.WriteSummaryAsync(new CompletedJobRunSummary
            {
                JobId = snapshot.SourceJobId,
                StartedUtc = runStartedUtc,
                RunType = "DirectUpload",
                Status = runStatus,
                Summary = runSummaryText,
                RequestedFiles = result.TotalRequestedFiles,
                PlannedFiles = result.PlannedFiles,
                UploadedFiles = result.SucceededFiles,
                FailedFiles = result.FailedFiles,
                SkippedFiles = result.SkippedFiles,
                ResumedFiles = result.ResumedFiles,
                CreatedFolders = result.CreatedFolders,
                ReportFileName = Path.GetFileName(reportPath),
                RunLogFileName = Path.GetFileName(runLogPath)
            });
            _completedJobLogStore.PruneExpired(DateTime.UtcNow);

            UpdateOnUi(() =>
            {
                NdImportSessions.Insert(0, new NdImportSessionView(DateTime.Now, runStatus, runSummaryText));
                StatusText = queueSucceeded
                    ? "Queued direct upload completed."
                    : "Queued direct upload finished with failures.";
                DirectUploadStatus = runSummaryText;
            });

            await LoadRecentJobsAsync();
            await LoadQueueJobsAsync();

            return queueSucceeded
                ? new UploadRunnerResult(true)
                : new UploadRunnerResult(false, "One or more files failed during upload.");
        }
        catch (OperationCanceledException)
        {
            return new UploadRunnerResult(false, "Queued upload canceled.");
        }
        catch (Exception ex)
        {
            return new UploadRunnerResult(false, ex.Message);
        }
        finally
        {
            IsDirectUploadBusy = false;
        }
    }

    private static string FormatQuickJobTitle(UploadQueueJobRecord job)
    {
        var shortId = job.QueueJobId.Length > 8 ? job.QueueJobId[..8] : job.QueueJobId;
        return $"{shortId} ({job.SourceJobId})";
    }

    private sealed class MainViewModelUploadRunner : IUploadRunner
    {
        private readonly MainViewModel _owner;

        public MainViewModelUploadRunner(MainViewModel owner)
        {
            _owner = owner;
        }

        public Task<UploadRunnerResult> RunAsync(UploadQueueJobRecord job, CancellationToken cancellationToken = default)
        {
            return _owner.RunQueuedUploadJobAsync(job, cancellationToken);
        }
    }
}

public sealed class UploadQueueJobView
{
    public UploadQueueJobView(UploadQueueJobRecord record)
    {
        QueueJobId = record.QueueJobId;
        QueueJobShortId = record.QueueJobId.Length > 8 ? record.QueueJobId[..8] : record.QueueJobId;
        SourceJobId = record.SourceJobId;
        CreatedDisplay = record.CreatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        ScheduledDisplay = record.ScheduledForUtc?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "--";
        State = record.State.ToString();
        SourceRoot = record.SourceRoot;
    }

    public string QueueJobId { get; }

    public string QueueJobShortId { get; }

    public string SourceJobId { get; }

    public string CreatedDisplay { get; }

    public string ScheduledDisplay { get; }

    public string State { get; }

    public string SourceRoot { get; }
}
