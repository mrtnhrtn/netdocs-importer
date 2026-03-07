namespace NetDocsImporter.Core;

public enum ExportMetadataFormat
{
    Json,
    Xml
}

public sealed class ExportConfig
{
    public string SourceCabinetId { get; set; } = string.Empty;

    public string SourceTargetId { get; set; } = string.Empty;

    public string SourceTargetType { get; set; } = string.Empty;

    public string DestinationRootPath { get; set; } = string.Empty;

    public bool AllVersions { get; set; }

    public ExportMetadataFormat MetadataFormat { get; set; } = ExportMetadataFormat.Json;

    public int Concurrency { get; set; } = 8;
}

public sealed class ExportPlan
{
    public ExportConfig Config { get; set; } = new();

    public List<ExportItem> Items { get; set; } = new();

    public int DocumentCount { get; set; }

    public int VersionCount { get; set; }

    public long EstimatedBytes { get; set; }

    public List<string> Warnings { get; set; } = new();
}

public sealed class ExportItem
{
    public string DocumentId { get; set; } = string.Empty;

    public string? VersionId { get; set; }

    public string SourcePath { get; set; } = string.Empty;

    public string LocalPath { get; set; } = string.Empty;

    public long? SizeBytes { get; set; }

    public List<ExportMetadataField> MetadataFields { get; set; } = new();
}

public sealed class ExportResult
{
    public DateTime StartedUtc { get; set; }

    public DateTime CompletedUtc { get; set; }

    public int Succeeded { get; set; }

    public int Failed { get; set; }

    public string ManifestPath { get; set; } = string.Empty;

    public string MetadataPath { get; set; } = string.Empty;

    public List<string> Errors { get; set; } = new();
}

public sealed class MetadataDump
{
    public string SourceCabinetId { get; set; } = string.Empty;

    public string SourceTargetId { get; set; } = string.Empty;

    public DateTime GeneratedUtc { get; set; }

    public List<MetadataDumpItem> Items { get; set; } = new();
}

public sealed class MetadataDumpItem
{
    public string DocumentId { get; set; } = string.Empty;

    public string? VersionId { get; set; }

    public string SourcePath { get; set; } = string.Empty;

    public string LocalPath { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string Error { get; set; } = string.Empty;

    public List<ExportMetadataField> MetadataFields { get; set; } = new();
}

public sealed class ExportMetadataField
{
    public string Name { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}
