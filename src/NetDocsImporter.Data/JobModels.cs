namespace NetDocsImporter.Data;

public sealed record JobRecord(string JobId, DateTime CreatedUtc, string SourceRoot, string Status);

public sealed record FileRecord(
    string FileId,
    string JobId,
    string FullPath,
    string RelativePath,
    long SizeBytes,
    DateTime ModifiedUtc,
    bool IsLargeWarning);

public sealed record JobSummary(
    string JobId,
    DateTime CreatedUtc,
    string SourceRoot,
    string Status,
    long FileCount,
    long TotalBytes,
    long LargeWarnings);
