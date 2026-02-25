using NetDocsImporter.Core;

namespace NetDocsImporter.Tests;

public sealed class ExportOutputWriterTests
{
    [Fact]
    public async Task WriteManifestAsync_WritesManifestJson()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var writer = new ExportOutputWriter();
            var items = new[]
            {
                new ExportItem
                {
                    DocumentId = "doc-1",
                    VersionId = "1",
                    SourcePath = "/workspace/folder/file.docx",
                    LocalPath = "workspace/folder/file.docx"
                }
            };

            var manifestPath = await writer.WriteManifestAsync(tempRoot, items);

            Assert.True(File.Exists(manifestPath));
            var content = await File.ReadAllTextAsync(manifestPath);
            Assert.Contains("\"documentId\": \"doc-1\"", content, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task WriteMetadataAsync_WritesXml_WhenRequested()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var writer = new ExportOutputWriter();
            var metadata = new MetadataDump
            {
                SourceCabinetId = "cab-1",
                SourceTargetId = "fld-1",
                GeneratedUtc = DateTime.UtcNow,
                Items = new List<MetadataDumpItem>
                {
                    new()
                    {
                        DocumentId = "doc-1",
                        SourcePath = "/workspace/folder/file.docx",
                        LocalPath = "workspace/folder/file.docx",
                        Status = "Succeeded"
                    }
                }
            };

            var metadataPath = await writer.WriteMetadataAsync(tempRoot, metadata, ExportMetadataFormat.Xml);

            Assert.True(File.Exists(metadataPath));
            Assert.EndsWith(".xml", metadataPath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    private static string CreateTempRoot()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "netdocs-export-output-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        return tempRoot;
    }

    private static void CleanupTempRoot(string tempRoot)
    {
        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }
}
