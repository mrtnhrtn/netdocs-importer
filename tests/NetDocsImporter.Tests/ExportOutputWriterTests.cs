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
                    VersionNumber = "1",
                    IsOfficialVersion = false,
                    VersionDiscoverySource = "versionList",
                    SourcePath = "/workspace/folder/file.docx",
                    LocalPath = "workspace/folder/file.docx",
                    SourceReferences = new List<ExportSourceReference>
                    {
                        new()
                        {
                            SourcePath = "/workspace/folder/file.docx",
                            ScopeKind = "Folder",
                            Disposition = "Exported",
                            Reason = "Chosen as the canonical export surface for this document/version."
                        },
                        new()
                        {
                            SourcePath = "/workspace/filter/file.docx",
                            ScopeKind = "WorkspaceFilter",
                            Disposition = "SkippedDuplicate",
                            Reason = "Skipped because this document/version was already planned from a preferred folder/workspace surface."
                        }
                    }
                }
            };

            var manifestPath = await writer.WriteManifestAsync(tempRoot, "run-1", items);

            Assert.True(File.Exists(manifestPath));
            Assert.EndsWith("manifest-run-1.json", manifestPath, StringComparison.OrdinalIgnoreCase);
            var content = await File.ReadAllTextAsync(manifestPath);
            Assert.Contains("\"documentId\": \"doc-1\"", content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"versionId\": \"1\"", content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"versionNumber\": \"1\"", content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"isOfficialVersion\": false", content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"versionDiscoverySource\": \"versionList\"", content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"sourceReferences\"", content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"disposition\": \"SkippedDuplicate\"", content, StringComparison.OrdinalIgnoreCase);
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

            var metadataPath = await writer.WriteMetadataAsync(tempRoot, "run-1", metadata, ExportMetadataFormat.Xml);

            Assert.True(File.Exists(metadataPath));
            Assert.EndsWith("metadata-run-1.xml", metadataPath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task WriteManifestAsync_PreservesPriorArtifactsAcrossRuns()
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
                    SourcePath = "/workspace/folder/file.docx",
                    LocalPath = "workspace/folder/file.docx"
                }
            };

            var firstPath = await writer.WriteManifestAsync(tempRoot, "run-1", items);
            var secondPath = await writer.WriteManifestAsync(tempRoot, "run-2", items);

            Assert.NotEqual(firstPath, secondPath);
            Assert.True(File.Exists(firstPath));
            Assert.True(File.Exists(secondPath));
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task WriteMetadataAsync_PreservesPriorArtifactsAcrossRuns()
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
                Items = new List<MetadataDumpItem>()
            };

            var firstPath = await writer.WriteMetadataAsync(tempRoot, "run-1", metadata, ExportMetadataFormat.Json);
            var secondPath = await writer.WriteMetadataAsync(tempRoot, "run-2", metadata, ExportMetadataFormat.Json);

            Assert.NotEqual(firstPath, secondPath);
            Assert.True(File.Exists(firstPath));
            Assert.True(File.Exists(secondPath));
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
