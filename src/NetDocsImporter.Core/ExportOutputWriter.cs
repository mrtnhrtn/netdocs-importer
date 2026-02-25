using System.Text.Json;
using System.Xml.Serialization;

namespace NetDocsImporter.Core;

public sealed class ExportOutputWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<string> WriteManifestAsync(string destinationRootPath, IReadOnlyList<ExportItem> items, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(destinationRootPath);
        var manifestPath = Path.Combine(destinationRootPath, "manifest.json");

        var manifest = new ExportManifest
        {
            GeneratedUtc = DateTime.UtcNow,
            Items = items.Select(item => new ExportManifestItem
            {
                DocumentId = item.DocumentId,
                VersionId = item.VersionId,
                SourcePath = item.SourcePath,
                LocalPath = item.LocalPath
            }).ToList()
        };

        await using var stream = new FileStream(manifestPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken);
        return manifestPath;
    }

    public async Task<string> WriteMetadataAsync(string destinationRootPath, MetadataDump metadata, ExportMetadataFormat format, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(destinationRootPath);
        if (format == ExportMetadataFormat.Xml)
        {
            var xmlPath = Path.Combine(destinationRootPath, "metadata.xml");
            var serializer = new XmlSerializer(typeof(MetadataDump));
            await using var stream = new FileStream(xmlPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            serializer.Serialize(stream, metadata);
            return xmlPath;
        }

        var jsonPath = Path.Combine(destinationRootPath, "metadata.json");
        await using (var stream = new FileStream(jsonPath, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            await JsonSerializer.SerializeAsync(stream, metadata, JsonOptions, cancellationToken);
        }

        return jsonPath;
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
}
