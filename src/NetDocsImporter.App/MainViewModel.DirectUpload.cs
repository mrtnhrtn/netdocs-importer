using System.Collections.ObjectModel;
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
    private bool _isDirectUploadBusy;
    private string _directUploadStatus = "Direct upload mode is available as preview.";

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

    public bool CanRunDirectUpload =>
        IsDirectApiMode &&
        !IsDirectUploadBusy &&
        IsNetDocumentsConnected &&
        _directUploadPlan is not null &&
        _directUploadPlan.CanUpload &&
        _directUploadPlan.Files.Count > 0;

    public async Task RefreshDirectUploadPreflightAsync()
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

        try
        {
            IsDirectUploadBusy = true;
            DirectUploadStatus = "Building direct upload plan...";

            var service = RequireDirectUploadService();
            var context = BuildDirectUploadPlanContext();
            var plan = await service.BuildPlanAsync(CurrentJobId, _selectedNetDocumentsTarget, context);
            SetDirectUploadPlan(plan);

            var errorCount = plan.Issues.Count(i => i.Severity == DirectUploadIssueSeverity.Error);
            var warningCount = plan.Issues.Count(i => i.Severity == DirectUploadIssueSeverity.Warning);
            var createdFolders = plan.Folders.Count(f => f.CreatedDuringPlanning);

            DirectUploadStatus =
                $"Plan ready: files={plan.Files.Count:N0}, folders={plan.Folders.Count:N0}, created={createdFolders:N0}, errors={errorCount:N0}, warnings={warningCount:N0}.";
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

        if (_directUploadPlan is null || _directUploadPlan.Files.Count == 0)
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
            StatusText = "Running direct upload...";
            var context = BuildDirectUploadPlanContext();
            var service = RequireDirectUploadService();
            var progress = new Progress<DirectUploadProgress>(p =>
            {
                DirectUploadStatus = $"Uploading {p.CompletedFiles:N0}/{p.TotalFiles:N0}: {p.CurrentRelativePath}";
            });

            var result = await service.UploadAsync(_directUploadPlan, context, progress);
            var reportPath = await WriteDirectUploadReportAsync(result);

            StatusText =
                $"Direct upload complete. Succeeded={result.SucceededFiles:N0}, Failed={result.FailedFiles:N0}. Report: {reportPath}";
            NdImportSessions.Insert(0, new NdImportSessionView(DateTime.Now, "DirectUpload", Path.GetFileName(reportPath)));
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

    private DirectUploadPlanContext BuildDirectUploadPlanContext()
    {
        return new DirectUploadPlanContext
        {
            CabinetId = SelectedNetDocumentsCabinetId,
            RepositoryId = SelectedNetDocumentsRepositoryId,
            EffectiveProfileDefaults = EffectiveProfileDefaults,
            AllowCreateFolders = true,
            RequireAcl = false
        };
    }

    private async Task<string> WriteDirectUploadReportAsync(DirectUploadRunResult result)
    {
        Directory.CreateDirectory(_paths.ReportsDirectory);
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
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

        await File.WriteAllLinesAsync(reportPath, lines, new UTF8Encoding(false));
        return reportPath;
    }

    private void SetDirectUploadPlan(UploadPlanResult? plan)
    {
        _directUploadPlan = plan;
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

    private static string EscapeCsv(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var escaped = value.Replace("\"", "\"\"", StringComparison.Ordinal);
        return escaped.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? $"\"{escaped}\"" : escaped;
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
