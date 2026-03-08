using NetDocsImporter.Core;

namespace NetDocsImporter.NetDocs;

public enum NdExportScopeKind
{
    Workspace,
    Folder,
    WorkspaceFilter,
    SavedSearch,
    Collabspace
}

public sealed class NdExportScope
{
    public string ContainerId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public NdTargetType TargetType { get; set; }

    public string Extension { get; set; } = string.Empty;

    public NdExportScopeKind Kind { get; set; } = NdExportScopeKind.Folder;

    public string? ParentContainerId { get; set; }

    public List<string> PathSegments { get; set; } = new();
}

public sealed class NdExportScopeEnumerationResult
{
    public List<NdExportScope> Scopes { get; set; } = new();

    public List<NdExportScopeTraversalIssue> Issues { get; set; } = new();

    public bool IsPartial => Issues.Count > 0;
}

public sealed class NdExportScopeTraversalIssue
{
    public string ContainerId { get; set; } = string.Empty;

    public string ScopeName { get; set; } = string.Empty;

    public NdTargetType TargetType { get; set; }

    public string Message { get; set; } = string.Empty;
}

public sealed class NdExportAttributeValue
{
    public string Name { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}

public sealed class NdExportDocumentVersion
{
    public string VersionId { get; set; } = string.Empty;

    public string VersionNumber { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public long? SizeBytes { get; set; }

    public bool IsOfficial { get; set; }

    public string DiscoverySource { get; set; } = string.Empty;

    public List<NdExportAttributeValue> Attributes { get; set; } = new();
}

public sealed class NdExportDocument
{
    public string DocumentId { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public long? SizeBytes { get; set; }

    public string? OfficialVersionId { get; set; }

    public string? OfficialVersionHint { get; set; }

    public int? KnownVersionCount { get; set; }

    public bool HasExactVersionCoverage { get; set; }

    public bool CoverageReliable { get; set; }

    public bool NeedsExpansion { get; set; }

    public List<NdExportDocumentVersion> VersionHints { get; set; } = new();

    public List<NdExportAttributeValue> StandardAttributes { get; set; } = new();

    public List<NdExportAttributeValue> CustomAttributes { get; set; } = new();
}

public sealed class NdBinaryDownloadResponse
{
    public bool Succeeded { get; set; }

    public int StatusCode { get; set; }

    public string RequestPath { get; set; } = string.Empty;

    public string ErrorMessage { get; set; } = string.Empty;

    public long BytesWritten { get; set; }

    public long? ContentLength { get; set; }

    public TimeSpan? RetryAfter { get; set; }
}
