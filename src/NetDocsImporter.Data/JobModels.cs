namespace NetDocsImporter.Data;

public sealed record JobRecord(
    string JobId,
    DateTime CreatedUtc,
    string SourceRoot,
    string Status,
    string? RepositoryId = null);

public sealed record FileRecord(
    string FileId,
    string JobId,
    string FullPath,
    string RelativePath,
    long SizeBytes,
    DateTime ModifiedUtc,
    bool IsLargeWarning,
    string? FolderId,
    string ImportMode,
    string? ImportReason);

public sealed record FolderRecord(
    string FolderId,
    string JobId,
    string FullPath,
    string RelativePath,
    string? ParentFolderId,
    int Depth,
    bool IsIncluded,
    bool IsOverride,
    DateTime CreatedUtc,
    string ImportMode,
    string ProfileMode);

public sealed record FolderImportCounts(
    string FolderId,
    long IncludedFileCount,
    long IncludedDescendantFileCount,
    bool EffectiveIncluded);

public sealed record JobSummary(
    string JobId,
    DateTime CreatedUtc,
    string SourceRoot,
    string Status,
    long FileCount,
    long TotalBytes,
    long LargeWarnings,
    string? RepositoryId = null);

public sealed record TransferRecord(
    string TransferId,
    string JobId,
    string FileId,
    int Attempt,
    string Status,
    DateTime? StartedUtc,
    DateTime? FinishedUtc,
    long? DurationMs,
    string? Error,
    int? WorkerId,
    int? SimulatedDelayMs,
    int? HttpStatus,
    string? ResponseSnippet);

public sealed record TransferState(string TransferId, string FileId, string Status, int Attempt);

public sealed record TransferSummary(
    string TransferId,
    string FileId,
    string RelativePath,
    string Status,
    int Attempt,
    long? DurationMs,
    string? Error);

public sealed record TransferStatusCounts(
    long Total,
    long Queued,
    long Running,
    long Succeeded,
    long Failed,
    long Canceled);

public sealed record NetDocumentsCabinetRecord(
    string CabinetId,
    string RepositoryId,
    string RepositoryName,
    string CabinetName,
    string Description,
    int? WorkspaceAttributeNum,
    string WorkspacePluralName,
    bool? AllowFileInWorkspaces,
    string Region,
    DateTime SyncedUtc);

public sealed record NetDocumentsAttributeRecord(
    string CabinetId,
    string RepositoryId,
    int AttributeNum,
    string AttributeId,
    string Name,
    string DataType,
    bool IsRequired,
    bool IsMultiValue,
    bool IsLookup,
    int? ParentAttributeNum,
    bool IsChildAttribute,
    DateTime SyncedUtc);

public sealed record NetDocumentsLookupValueRecord(
    string CabinetId,
    int AttributeNum,
    string? ParentKey,
    string ValueKey,
    string Description,
    DateTime SyncedUtc);

public sealed record NetDocumentsProfileContextSnapshotRecord(
    string CabinetId,
    string RepositoryId,
    int AttributeCount,
    int RequiredAttributeCount,
    int LookupAttributeCount,
    int LookupValueCount,
    DateTime? LastSyncedUtc);

public sealed record NetDocumentsWorkspaceCacheRecord(
    string UserKey,
    string ServiceKey,
    string CabinetScope,
    string WorkspaceId,
    string WorkspaceName,
    string TargetType,
    string? ParentWorkspaceId,
    string Extension,
    string PathDisplay,
    DateTime UpdatedUtc);
