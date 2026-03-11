using System.Text.Json;
using System.Xml.Serialization;

namespace NetDocsImporter.Core;

public sealed class ExportOutputWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<string> WriteManifestAsync(string destinationRootPath, string artifactId, IReadOnlyList<ExportItem> items, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(destinationRootPath);
        var manifestPath = BuildArtifactPath(destinationRootPath, "manifest", artifactId, ".json");

        var manifest = new ExportManifest
        {
            GeneratedUtc = DateTime.UtcNow,
            Items = items.Select(item => new ExportManifestItem
            {
                DocumentId = item.DocumentId,
                VersionId = item.VersionId,
                SourcePath = item.SourcePath,
                LocalPath = item.LocalPath,
                SourceReferences = item.SourceReferences
                    .Select(reference => new ExportSourceReference
                    {
                        SourcePath = reference.SourcePath,
                        ScopeKind = reference.ScopeKind,
                        Disposition = reference.Disposition,
                        Reason = reference.Reason
                    })
                    .ToList()
            }).ToList()
        };

        await using var stream = new FileStream(manifestPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken);
        return manifestPath;
    }

    public async Task<string> WriteMetadataAsync(string destinationRootPath, string artifactId, MetadataDump metadata, ExportMetadataFormat format, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(destinationRootPath);
        if (format == ExportMetadataFormat.Xml)
        {
            var xmlPath = BuildArtifactPath(destinationRootPath, "metadata", artifactId, ".xml");
            var serializer = new XmlSerializer(typeof(MetadataDump));
            await using var stream = new FileStream(xmlPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            serializer.Serialize(stream, metadata);
            return xmlPath;
        }

        var jsonPath = BuildArtifactPath(destinationRootPath, "metadata", artifactId, ".json");
        await using (var stream = new FileStream(jsonPath, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            await JsonSerializer.SerializeAsync(stream, metadata, JsonOptions, cancellationToken);
        }

        return jsonPath;
    }

    private static string BuildArtifactPath(string destinationRootPath, string prefix, string artifactId, string extension)
    {
        var sanitizedArtifactId = string.IsNullOrWhiteSpace(artifactId)
            ? DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff")
            : string.Concat(artifactId.Trim().Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-'));

        return Path.Combine(destinationRootPath, $"{prefix}-{sanitizedArtifactId}{extension}");
    }
}

public sealed class ExportManifest
{
    public DateTime GeneratedUtc { get; set; }

    public List<ExportManifestItem> Items { get; set; } = new();
}

public sealed class ExportManifestItem
{
    public string DocumentId { get; set; } = string.Empty;

    public string? VersionId { get; set; }

    public string SourcePath { get; set; } = string.Empty;

    public string LocalPath { get; set; } = string.Empty;

    public List<ExportSourceReference> SourceReferences { get; set; } = new();
}
