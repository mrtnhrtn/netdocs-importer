namespace NetDocsImporter.Core;

public enum ImportExecutionMode
{
    NdImportCsv,
    DirectApi
}

public enum DirectUploadIssueSeverity
{
    Info,
    Warning,
    Error
}

public sealed record DirectUploadIssue(
    DirectUploadIssueSeverity Severity,
    string Code,
    string Message,
    string? RelativePath = null);

public sealed record UploadPlanFolderEntry(
    string RelativePath,
    string ContainerId,
    bool CreatedDuringPlanning);

public sealed record UploadPlanFileEntry(
    string FileId,
    string RelativePath,
    string FullPath,
    string DestinationContainerId,
    IReadOnlyDictionary<string, string> ProfileValues,
    string? Acl);

public sealed class UploadPlanResult
{
    public IReadOnlyList<UploadPlanFolderEntry> Folders { get; init; } = Array.Empty<UploadPlanFolderEntry>();

    public IReadOnlyList<UploadPlanFileEntry> Files { get; init; } = Array.Empty<UploadPlanFileEntry>();

    public IReadOnlyList<DirectUploadIssue> Issues { get; init; } = Array.Empty<DirectUploadIssue>();

    public int TotalRequestedFiles { get; init; }

    public int PlannedFiles { get; init; }

    public int SkippedFiles { get; init; }

    public int PlannedFolderCreates { get; init; }

    public bool CanUpload { get; init; }
}

public sealed class DirectUploadPlanContext
{
    public string CabinetId { get; init; } = string.Empty;

    public string RepositoryId { get; init; } = string.Empty;

    public EffectiveProfileDefaults EffectiveProfileDefaults { get; init; } = EffectiveProfileDefaults.Empty;

    public bool AllowCreateFolders { get; init; }

    public bool RequireAcl { get; init; }

    public string? DefaultAcl { get; init; }
}

public sealed record DirectUploadProgress(
    int CompletedFiles,
    int TotalFiles,
    string CurrentRelativePath);

public sealed record DirectUploadFileResult(
    string RelativePath,
    bool Succeeded,
    int HttpStatus,
    string Message);

public sealed class DirectUploadRunResult
{
    public IReadOnlyList<DirectUploadFileResult> Files { get; init; } = Array.Empty<DirectUploadFileResult>();

    public int TotalRequestedFiles { get; init; }

    public int PlannedFiles { get; init; }

    public int SkippedFiles { get; init; }

    public int CreatedFolders { get; init; }

    public int SucceededFiles { get; init; }

    public int FailedFiles { get; init; }
}

public interface IDestinationFolderResolver
{
    Task<UploadPlanResult> BuildPlanAsync(
        string jobId,
        NdTargetSelection target,
        DirectUploadPlanContext context,
        CancellationToken cancellationToken = default);
}

public interface IDirectUploadService : IDestinationFolderResolver
{
    Task<DirectUploadRunResult> UploadAsync(
        UploadPlanResult plan,
        DirectUploadPlanContext context,
        IProgress<DirectUploadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
