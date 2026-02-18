using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using NetDocsImporter.Core;

namespace NetDocsImporter.App;

public sealed partial class MainViewModel
{
    private readonly ObservableCollection<DirectUploadIssueView> _directUploadPreflightIssues = new();
    private readonly IReadOnlyList<ImportExecutionMode> _importExecutionModeOptions =
        new[] { ImportExecutionMode.NdImportCsv, ImportExecutionMode.DirectApi };

    private ImportExecutionMode _selectedImportExecutionMode = ImportExecutionMode.NdImportCsv;
    private UploadPlanResult? _directUploadPlan;
    private string _directUploadPlanJobId = string.Empty;
    private string _directUploadPlanTargetKey = string.Empty;
    private string _directUploadPlanRepositoryId = string.Empty;
    private string _directUploadPlanCabinetId = string.Empty;
    private bool _isDirectUploadBusy;
    private string _directUploadStatus = "Direct upload mode is available as preview.";
    private double _directUploadProgressPercent;
    private string? _lastDirectUploadLogPath;
    private string? _lastDirectUploadReportPath;

    public IReadOnlyList<ImportExecutionMode> ImportExecutionModeOptions => _importExecutionModeOptions;

    public ImportExecutionMode SelectedImportExecutionMode
    {
        get => _selectedImportExecutionMode;
        set
        {
            if (!SetField(ref _selectedImportExecutionMode, value))
            {
                return;
            }

            QueueSettingsSave();
            OnPropertyChanged(nameof(IsNdImportCsvMode));
            OnPropertyChanged(nameof(IsDirectApiMode));
            OnPropertyChanged(nameof(CanRunDirectUpload));

            if (value == ImportExecutionMode.DirectApi)
            {
                _ = RefreshDirectUploadPreflightAsync();
            }
        }
    }

    public bool IsNdImportCsvMode => SelectedImportExecutionMode == ImportExecutionMode.NdImportCsv;

    public bool IsDirectApiMode => SelectedImportExecutionMode == ImportExecutionMode.DirectApi;

    public ObservableCollection<DirectUploadIssueView> DirectUploadPreflightIssues => _directUploadPreflightIssues;

    public bool IsDirectUploadBusy
    {
        get => _isDirectUploadBusy;
        private set
        {
            if (SetField(ref _isDirectUploadBusy, value))
            {
                OnPropertyChanged(nameof(CanRunDirectUpload));
            }
        }
    }

    public string DirectUploadStatus
    {
        get => _directUploadStatus;
        private set => SetField(ref _directUploadStatus, value);
    }

    public double DirectUploadProgressPercent
    {
        get => _directUploadProgressPercent;
        private set => SetField(ref _directUploadProgressPercent, value);
    }

    public string DirectUploadProgressPercentDisplay => $"{DirectUploadProgressPercent:0.##}%";

    public bool CanExportDirectUploadLog => !string.IsNullOrWhiteSpace(_lastDirectUploadLogPath) && File.Exists(_lastDirectUploadLogPath);
    public bool CanOpenLastDirectUploadReport => !string.IsNullOrWhiteSpace(_lastDirectUploadReportPath) && File.Exists(_lastDirectUploadReportPath);

    public bool CanRunDirectUpload =>
        IsDirectApiMode &&
        !IsDirectUploadBusy &&
        IsNetDocumentsConnected &&
        _directUploadPlan is not null &&
        IsDirectUploadPlanAlignedWithCurrentContext() &&
        _directUploadPlan.CanUpload &&
        _directUploadPlan.Files.Count > 0;

    public async Task RefreshDirectUploadPreflightAsync(bool forceRescan = false)
    {
        if (!IsDirectApiMode)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(CurrentJobId))
        {
            DirectUploadStatus = "Select a source folder and scan files before direct upload.";
            SetDirectUploadPlan(null);
            return;
        }

        if (!CanConfirmNetDocumentsTarget || _selectedNetDocumentsTarget is null)
        {
            DirectUploadStatus = "Select and confirm a NetDocuments target before direct upload.";
            SetDirectUploadPlan(null);
            return;
        }

        if (forceRescan && await TryRescanSourceFolderBeforePreflightAsync())
        {
            return;
        }

        try
        {
            IsDirectUploadBusy = true;
            DirectUploadProgressPercent = 0;
            OnPropertyChanged(nameof(DirectUploadProgressPercentDisplay));
            DirectUploadStatus = "Building direct upload plan...";

            var service = RequireDirectUploadService();
            var context = BuildDirectUploadPlanContext(allowCreateFolders: false);
            var plan = await service.BuildPlanAsync(CurrentJobId, _selectedNetDocumentsTarget, context);
            SetDirectUploadPlan(plan);

            var errorCount = plan.Issues.Count(i => i.Severity == DirectUploadIssueSeverity.Error);
            var warningCount = plan.Issues.Count(i => i.Severity == DirectUploadIssueSeverity.Warning);
            var infoCount = plan.Issues.Count(i => i.Severity == DirectUploadIssueSeverity.Info);
            var plannedFolderCreates = plan.PlannedFolderCreates;
            var skippedSummary = DirectUploadIssueUtilities.BuildSkippedFilesSummary(plan.Issues, maxInline: 3);
            var firstBlockingIssue = plan.Issues
                .Where(i => i.Severity == DirectUploadIssueSeverity.Error)
                .OrderBy(GetBlockingIssuePriority)
                .FirstOrDefault();

            if (errorCount > 0)
            {
                DirectUploadStatus =
                    $"Plan blocked: requested={plan.TotalRequestedFiles:N0}, planned={plan.PlannedFiles:N0}, skipped={plan.SkippedFiles:N0}, folders={plan.Folders.Count:N0}, wouldCreate={plannedFolderCreates:N0}, errors={errorCount:N0}, warnings={warningCount:N0}, info={infoCount:N0}.";

                if (firstBlockingIssue is not null)
                {
                    var blockerContext = string.IsNullOrWhiteSpace(firstBlockingIssue.RelativePath)
                        ? firstBlockingIssue.Message
                        : $"{firstBlockingIssue.Message} ({firstBlockingIssue.RelativePath})";
                    DirectUploadStatus = $"{DirectUploadStatus} Blocker: {blockerContext}";
                }
            }
            else if (plan.PlannedFiles == 0)
            {
                DirectUploadStatus =
                    $"No uploadable files remain after preflight. requested={plan.TotalRequestedFiles:N0}, planned={plan.PlannedFiles:N0}, skipped={plan.SkippedFiles:N0}, errors={errorCount:N0}, warnings={warningCount:N0}, info={infoCount:N0}.";
            }
            else
            {
                DirectUploadStatus =
                    $"Plan ready: requested={plan.TotalRequestedFiles:N0}, planned={plan.PlannedFiles:N0}, skipped={plan.SkippedFiles:N0}, folders={plan.Folders.Count:N0}, wouldCreate={plannedFolderCreates:N0}, errors={errorCount:N0}, warnings={warningCount:N0}, info={infoCount:N0}.";
            }

            if (!string.IsNullOrWhiteSpace(skippedSummary))
            {
                DirectUploadStatus = $"{DirectUploadStatus} {skippedSummary}";
            }

            Trace.WriteLine($"ND-DIRECT preflight status '{DirectUploadStatus}'.");
        }
        catch (Exception ex)
        {
            SetDirectUploadPlan(null);
            DirectUploadStatus = $"Direct upload preflight failed: {ex.Message}";
        }
        finally
        {
            IsDirectUploadBusy = false;
        }
    }

    public async Task RunDirectUploadAsync()
    {
        if (!IsDirectApiMode)
        {
            StatusText = "Select DirectApi execution mode to run direct upload.";
            return;
        }

        if (_directUploadPlan is null ||
            _directUploadPlan.Files.Count == 0 ||
            !IsDirectUploadPlanAlignedWithCurrentContext())
        {
            await RefreshDirectUploadPreflightAsync();
        }

        if (_directUploadPlan is null)
        {
            StatusText = "Direct upload plan is unavailable.";
            return;
        }

        if (!_directUploadPlan.CanUpload)
        {
            StatusText = "Direct upload blocked by preflight errors. Resolve issues first.";
            return;
        }

        var runButton = new TaskDialogButton("Run direct upload");
        var cancelButton = TaskDialogButton.Cancel;
        var prompt = new TaskDialogPage
        {
            Caption = "Direct Upload",
            Heading = "Upload files directly to NetDocuments?",
            Text = $"Files: {_directUploadPlan.Files.Count:N0}{Environment.NewLine}" +
                   $"Target: {SelectedNetDocumentsTargetName}{Environment.NewLine}" +
                   $"This action uploads content via NetDocuments API.",
            Buttons = { runButton, cancelButton }
        };

        if (ShowTaskDialog(prompt) != runButton)
        {
            StatusText = "Direct upload canceled.";
            return;
        }

        try
        {
            IsDirectUploadBusy = true;
            DirectUploadProgressPercent = 0;
            OnPropertyChanged(nameof(DirectUploadProgressPercentDisplay));
            StatusText = "Running direct upload...";
            var service = RequireDirectUploadService();
            DirectUploadStatus = "Materializing direct upload plan...";
            var executionContext = BuildDirectUploadPlanContext(allowCreateFolders: true);
            var currentJobId = CurrentJobId;
            if (string.IsNullOrWhiteSpace(currentJobId))
            {
                StatusText = "Direct upload plan is unavailable.";
                return;
            }

            var executionPlan = await service.BuildPlanAsync(currentJobId, _selectedNetDocumentsTarget!, executionContext);
            SetDirectUploadPlan(executionPlan);

            if (!executionPlan.CanUpload || executionPlan.Files.Count == 0)
            {
                StatusText = "Direct upload blocked during execution plan materialization. Resolve reported issues and try again.";
                return;
            }

            var runStartedUtc = DateTime.UtcNow;

            var progress = new Progress<DirectUploadProgress>(p =>
            {
                var percent = p.PercentComplete;
                if (percent <= 0 && p.TotalFiles > 0)
                {
                    percent = Math.Round((double)p.CompletedFiles / p.TotalFiles * 100d, 2);
                }

                DirectUploadProgressPercent = percent;
                OnPropertyChanged(nameof(DirectUploadProgressPercentDisplay));
                DirectUploadStatus = $"Uploading {p.CompletedFiles:N0}/{p.TotalFiles:N0} ({percent:0.##}%): {p.CurrentRelativePath}";
            });

            var result = await service.UploadAsync(executionPlan, executionContext, progress);
            var reportPath = await WriteDirectUploadReportAsync(executionPlan, result, runStartedUtc);
            var runLogPath = await WriteDirectUploadRunLogAsync(executionPlan, result, reportPath, runStartedUtc);
            _lastDirectUploadReportPath = reportPath;
            _lastDirectUploadLogPath = runLogPath;
            OnPropertyChanged(nameof(CanOpenLastDirectUploadReport));
            OnPropertyChanged(nameof(CanExportDirectUploadLog));
            DirectUploadProgressPercent = 100;
            OnPropertyChanged(nameof(DirectUploadProgressPercentDisplay));

            StatusText =
                $"Direct upload complete. Uploaded {result.SucceededFiles:N0}/{result.TotalRequestedFiles:N0} (skipped {result.SkippedFiles:N0}, resumed {result.ResumedFiles:N0}). Created folders={result.CreatedFolders:N0}. Succeeded={result.SucceededFiles:N0}, Failed={result.FailedFiles:N0}.";
            DirectUploadStatus =
                $"Direct upload complete. CSV report: {Path.GetFileName(reportPath)}";

            var runStatus = result.FailedFiles > 0 || result.SkippedFiles > 0 ? "DirectUpload Partial" : "DirectUpload";
            var runSummaryText =
                $"Uploaded {result.SucceededFiles:N0}/{result.TotalRequestedFiles:N0} (Skipped {result.SkippedFiles:N0}, Resumed {result.ResumedFiles:N0}), Created {result.CreatedFolders:N0}, Failed {result.FailedFiles:N0}, report {Path.GetFileName(reportPath)}";
            NdImportSessions.Insert(0, new NdImportSessionView(
                DateTime.Now,
                runStatus,
                runSummaryText));

            await _completedJobLogStore.WriteSummaryAsync(new CompletedJobRunSummary
            {
                JobId = currentJobId,
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

            foreach (var skippedIssue in executionPlan.Issues.Where(DirectUploadIssueUtilities.IsSkippedFileIssue))
            {
                var reason = GetSkippedFileReason(skippedIssue.Code);
                NdImportSessions.Insert(0, new NdImportSessionView(
                    DateTime.Now,
                    "DirectUpload Skip",
                    $"{result.SucceededFiles:N0}/{result.TotalRequestedFiles:N0} complete; skipped {result.SkippedFiles:N0}; {reason}: {skippedIssue.RelativePath ?? string.Empty}"));
            }

            await LoadRecentJobsAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Direct upload failed: {ex.Message}";
        }
        finally
        {
            IsDirectUploadBusy = false;
        }
    }

    private DirectUploadPlanContext BuildDirectUploadPlanContext(bool allowCreateFolders)
    {
        var netDocuments = GetOrCreateNetDocumentsSettings();
        int? v1DocumentIndexPriority = netDocuments.DirectUploadV1DocumentIndexPriority;
        if (!v1DocumentIndexPriority.HasValue || v1DocumentIndexPriority.Value <= 0)
        {
            v1DocumentIndexPriority = 400;
        }

        return new DirectUploadPlanContext
        {
            JobId = CurrentJobId ?? string.Empty,
            CabinetId = SelectedNetDocumentsCabinetId,
            RepositoryId = SelectedNetDocumentsRepositoryId,
            EffectiveProfileDefaults = EffectiveProfileDefaults,
            AllowCreateFolders = allowCreateFolders,
            RequireAcl = false,
            MaxConcurrency = Math.Clamp(MaxConcurrency, 1, 8),
            MaxRetryAttempts = 4,
            V1DocumentIndexPriority = v1DocumentIndexPriority
        };
    }

    private async Task<bool> TryRescanSourceFolderBeforePreflightAsync()
    {
        if (IsScanning)
        {
            DirectUploadStatus = "Source folder scan already in progress...";
            return true;
        }

        if (string.IsNullOrWhiteSpace(CurrentJobSourceRoot))
        {
            return false;
        }

        if (!Directory.Exists(CurrentJobSourceRoot))
        {
            DirectUploadStatus = $"Source folder not found: {CurrentJobSourceRoot}";
            SetDirectUploadPlan(null);
            return true;
        }

        if (!string.Equals(SelectedFolder, CurrentJobSourceRoot, StringComparison.OrdinalIgnoreCase))
        {
            SelectedFolder = CurrentJobSourceRoot;
        }

        DirectUploadStatus = "Rescanning source folder before refreshing preflight...";
        Trace.WriteLine($"ND-DIRECT preflight refresh requested; rescanning source root '{CurrentJobSourceRoot}'.");
        await StartScanAsync(CurrentJobSourceRoot);
        return true;
    }

    private async Task<string> WriteDirectUploadReportAsync(
        UploadPlanResult plan,
        DirectUploadRunResult result,
        DateTime runStartedUtc)
    {
        Directory.CreateDirectory(_paths.ReportsDirectory);
        var timestamp = runStartedUtc.ToLocalTime().ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var job = CurrentJobId ?? "unknown";
        var reportPath = Path.Combine(_paths.ReportsDirectory, $"directupload-{job}-{timestamp}.csv");

        var lines = new List<string>
        {
            "RELATIVE PATH,STATUS,HTTP STATUS,MESSAGE"
        };
        foreach (var file in result.Files)
        {
            lines.Add(string.Join(",", new[]
            {
                EscapeCsv(file.RelativePath),
                file.Succeeded ? "Succeeded" : "Failed",
                file.HttpStatus.ToString(CultureInfo.InvariantCulture),
                EscapeCsv(file.Message)
            }));
        }

        foreach (var createdFolder in plan.Folders.Where(f => f.CreatedDuringPlanning))
        {
            lines.Add(string.Join(",", new[]
            {
                EscapeCsv(createdFolder.RelativePath),
                "FolderCreated",
                "200",
                EscapeCsv("Folder created during execution plan materialization.")
            }));
        }

        foreach (var skipped in plan.Issues.Where(DirectUploadIssueUtilities.IsSkippedFileIssue))
        {
            lines.Add(string.Join(",", new[]
            {
                EscapeCsv(skipped.RelativePath ?? string.Empty),
                "Skipped",
                "0",
                EscapeCsv(skipped.Message)
            }));
        }

        await File.WriteAllLinesAsync(reportPath, lines, new UTF8Encoding(false));
        return reportPath;
    }

    private async Task<string> WriteDirectUploadRunLogAsync(
        UploadPlanResult plan,
        DirectUploadRunResult result,
        string reportPath,
        DateTime runStartedUtc)
    {
        var job = CurrentJobId ?? "unknown";

        var builder = new StringBuilder();
        builder.AppendLine("+------------------------------------------------------------+");
        builder.AppendLine("|                 NetDocs Importer Run Log                  |");
        builder.AppendLine("+------------------------------------------------------------+");
        builder.AppendLine($" Started: {runStartedUtc.ToLocalTime():g}");
        builder.AppendLine($" Job Id: {job}");
        builder.AppendLine($" Target: {SelectedNetDocumentsTargetName}");
        builder.AppendLine($" Requested: {result.TotalRequestedFiles:N0}");
        builder.AppendLine($" Planned: {result.PlannedFiles:N0}");
        builder.AppendLine($" Uploaded: {result.SucceededFiles:N0}");
        builder.AppendLine($" Failed: {result.FailedFiles:N0}");
        builder.AppendLine($" Skipped: {result.SkippedFiles:N0}");
        builder.AppendLine($" Resumed: {result.ResumedFiles:N0}");
        builder.AppendLine($" CreatedFolders: {result.CreatedFolders:N0}");
        builder.AppendLine($" CSV Report: {reportPath}");
        builder.AppendLine("+------------------------------------------------------------+");
        builder.AppendLine("| File Outcomes                                              |");
        builder.AppendLine("+------------------------------------------------------------+");

        foreach (var file in result.Files.OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            var status = file.Succeeded ? "OK" : "FAIL";
            builder.AppendLine($" [{status}] {file.RelativePath}");
            builder.AppendLine($"       http={file.HttpStatus} message={file.Message}");
        }

        builder.AppendLine("+------------------------------------------------------------+");
        builder.AppendLine("| Planned Folder Mutations                                   |");
        builder.AppendLine("+------------------------------------------------------------+");
        foreach (var folder in plan.Folders.Where(f => f.CreatedDuringPlanning))
        {
            builder.AppendLine($" [CREATE] {folder.RelativePath} -> {folder.ContainerId}");
        }

        builder.AppendLine("+------------------------------------------------------------+");
        builder.AppendLine("| Preflight Issues                                           |");
        builder.AppendLine("+------------------------------------------------------------+");
        foreach (var issue in plan.Issues)
        {
            builder.AppendLine($" [{issue.Severity}] {issue.Code} :: {issue.Message} :: {issue.RelativePath}");
        }

        return await _completedJobLogStore.WriteRunLogAsync(job, runStartedUtc, builder.ToString());
    }

    private static string GetSkippedFileReason(string? issueCode)
    {
        if (string.Equals(issueCode, "ZERO_BYTE_FILE_SKIPPED", StringComparison.OrdinalIgnoreCase))
        {
            return "0KB file skipped";
        }

        if (string.Equals(issueCode, "MISSING_FILE_SKIPPED", StringComparison.OrdinalIgnoreCase))
        {
            return "Missing file skipped";
        }

        if (string.Equals(issueCode, "MISSING_EXTENSION_FILE_SKIPPED", StringComparison.OrdinalIgnoreCase))
        {
            return "Extensionless file skipped";
        }

        return "File skipped";
    }

    public async Task ExportLastDirectUploadLogAsync(string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(_lastDirectUploadLogPath) || !File.Exists(_lastDirectUploadLogPath))
        {
            throw new InvalidOperationException("No direct upload run log is available to export.");
        }

        var destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        await using var sourceStream = File.OpenRead(_lastDirectUploadLogPath);
        await using var destinationStream = File.Create(destinationPath);
        await sourceStream.CopyToAsync(destinationStream);
    }

    public void OpenLastDirectUploadReport()
    {
        if (string.IsNullOrWhiteSpace(_lastDirectUploadReportPath) || !File.Exists(_lastDirectUploadReportPath))
        {
            StatusText = "No direct upload CSV report is available to open.";
            return;
        }

        OpenFile(_lastDirectUploadReportPath);
    }

    private void SetDirectUploadPlan(UploadPlanResult? plan)
    {
        _directUploadPlan = plan;
        if (plan is null)
        {
            _directUploadPlanJobId = string.Empty;
            _directUploadPlanTargetKey = string.Empty;
            _directUploadPlanRepositoryId = string.Empty;
            _directUploadPlanCabinetId = string.Empty;
        }
        else
        {
            _directUploadPlanJobId = CurrentJobId ?? string.Empty;
            _directUploadPlanTargetKey = _selectedNetDocumentsTarget is null
                ? string.Empty
                : NdTargetBrowserLogic.BuildTargetKey(_selectedNetDocumentsTarget);
            _directUploadPlanRepositoryId = SelectedNetDocumentsRepositoryId ?? string.Empty;
            _directUploadPlanCabinetId = SelectedNetDocumentsCabinetId ?? string.Empty;
        }

        _directUploadPreflightIssues.Clear();
        if (plan is not null)
        {
            foreach (var issue in plan.Issues)
            {
                _directUploadPreflightIssues.Add(new DirectUploadIssueView(
                    issue.Severity.ToString(),
                    issue.Code,
                    issue.Message,
                    issue.RelativePath ?? string.Empty));
            }
        }

        OnPropertyChanged(nameof(CanRunDirectUpload));
    }

    private void InvalidateDirectUploadPlan(string reason)
    {
        SetDirectUploadPlan(null);
        DirectUploadStatus = reason;
    }

    private bool IsDirectUploadPlanAlignedWithCurrentContext()
    {
        if (_directUploadPlan is null || _selectedNetDocumentsTarget is null)
        {
            return false;
        }

        var currentTargetKey = NdTargetBrowserLogic.BuildTargetKey(_selectedNetDocumentsTarget);
        return string.Equals(_directUploadPlanJobId, CurrentJobId ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(_directUploadPlanTargetKey, currentTargetKey, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(_directUploadPlanRepositoryId, SelectedNetDocumentsRepositoryId ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(_directUploadPlanCabinetId, SelectedNetDocumentsCabinetId ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private void HandleDirectUploadContextChanged(string reason, bool refreshPreflight)
    {
        if (_directUploadPlan is null && !IsDirectApiMode)
        {
            return;
        }

        InvalidateDirectUploadPlan(reason);
        OnPropertyChanged(nameof(CanRunDirectUpload));

        if (!refreshPreflight ||
            !IsDirectApiMode ||
            IsDirectUploadBusy ||
            string.IsNullOrWhiteSpace(CurrentJobId) ||
            _selectedNetDocumentsTarget is null ||
            !_selectedNetDocumentsTargetSupported)
        {
            return;
        }

        _ = RefreshDirectUploadPreflightAsync();
    }

    private static string EscapeCsv(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var escaped = value.Replace("\"", "\"\"", StringComparison.Ordinal);
        return escaped.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? $"\"{escaped}\"" : escaped;
    }

    private static int GetBlockingIssuePriority(DirectUploadIssue issue)
    {
        return issue.Code switch
        {
            "FOLDER_CREATE_FORBIDDEN" => 0,
            "SAVED_SEARCH_UPLOAD_SCOPE_UNRESOLVED" => 1,
            _ => 10
        };
    }

    private static ImportExecutionMode ParseImportExecutionMode(string? rawValue)
    {
        if (Enum.TryParse<ImportExecutionMode>(rawValue, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return ImportExecutionMode.NdImportCsv;
    }
}

public sealed class DirectUploadIssueView
{
    public DirectUploadIssueView(string severity, string code, string message, string relativePath)
    {
        Severity = severity;
        Code = code;
        Message = message;
        RelativePath = relativePath;
    }

    public string Severity { get; }

    public string Code { get; }

    public string Message { get; }

    public string RelativePath { get; }
}
