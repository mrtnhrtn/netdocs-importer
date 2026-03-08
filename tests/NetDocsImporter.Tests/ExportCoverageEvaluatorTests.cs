using NetDocsImporter.Core;

namespace NetDocsImporter.Tests;

public sealed class ExportCoverageEvaluatorTests
{
    [Fact]
    public void AssessAllVersionsCoverage_ReturnsNoBlockingIssueWhenAllDocumentsHaveCoverage()
    {
        var assessment = ExportCoverageEvaluator.AssessAllVersionsCoverage(
            Array.Empty<string>(),
            Array.Empty<string>());

        Assert.False(assessment.HasBlockingIssue);
        Assert.Equal(0, assessment.UnknownCoverageDocumentCount);
        Assert.Equal(0, assessment.MissingExactVersionIdsDocumentCount);
        Assert.Equal(string.Empty, assessment.Message);
    }

    [Fact]
    public void AssessAllVersionsCoverage_ReturnsBlockingIssueWithSplitCategories()
    {
        var assessment = ExportCoverageEvaluator.AssessAllVersionsCoverage(
            ["DOC-3", "DOC-4"],
            ["Doc A.docx", "Doc B.pdf"],
            sampleSize: 2);

        Assert.True(assessment.HasBlockingIssue);
        Assert.Equal(2, assessment.UnknownCoverageDocumentCount);
        Assert.Equal(2, assessment.MissingExactVersionIdsDocumentCount);
        Assert.Contains("reported multiple versions, but exact version ids were not returned", assessment.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("did not return enough `VersionsLite` detail", assessment.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Doc A.docx", assessment.Message, StringComparison.Ordinal);
        Assert.Contains("Doc B.pdf", assessment.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("DOC-3", assessment.Message, StringComparison.Ordinal);
    }
}
