using NetDocsImporter.Core;

namespace NetDocsImporter.Tests;

public sealed class ExportPathResolverTests
{
    [Fact]
    public void ResolveRelativePath_SanitizesInvalidCharactersAndReservedNames()
    {
        var resolver = new ExportPathResolver();

        var relativePath = resolver.ResolveRelativePath(
            sourceSegments: new[] { "Client: A", "CON", "Folder*" },
            fileName: "draft?.docx",
            stableId: "doc-123");

        Assert.Equal("Client_ A/_CON/Folder_/draft_.docx", relativePath);
    }

    [Fact]
    public void ResolveRelativePath_AppliesDeterministicShortening_WhenPathTooLong()
    {
        var resolver = new ExportPathResolver(maxRelativePathLength: 90);
        var longSegment = new string('a', 80);
        var longFileName = new string('b', 120) + ".pdf";

        var relativePath = resolver.ResolveRelativePath(
            sourceSegments: new[] { longSegment, longSegment, longSegment },
            fileName: longFileName,
            stableId: "doc-long-001");

        Assert.True(relativePath.Length <= 90);
        Assert.Contains(".pdf", relativePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveCollision_AppendsStableHashSuffix()
    {
        var resolver = new ExportPathResolver();
        var first = resolver.ResolveCollision("root/folder/report.docx", "doc-1");
        var second = resolver.ResolveCollision("root/folder/report.docx", "doc-2");

        Assert.NotEqual(first, second);
        Assert.StartsWith("root/folder/report-", first, StringComparison.Ordinal);
        Assert.EndsWith(".docx", first, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveRelativePath_AppendsExtensionWhenMissing()
    {
        var resolver = new ExportPathResolver();

        var relativePath = resolver.ResolveRelativePath(
            sourceSegments: new[] { "Client A" },
            fileName: "Invoice_1",
            stableId: "doc-123",
            fileExtension: "pdf");

        Assert.Equal("Client A/Invoice_1.pdf", relativePath);
    }
}
