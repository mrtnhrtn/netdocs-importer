using NetDocsImporter.Core;

namespace NetDocsImporter.Tests;

public class DirectUploadIssueUtilitiesTests
{
    [Fact]
    public void BuildSkippedFilesSummary_IncludesSingleSkippedPath()
    {
        var issues = new[]
        {
            new DirectUploadIssue(DirectUploadIssueSeverity.Info, "ZERO_BYTE_FILE_SKIPPED", "File is zero bytes and was skipped from upload.", "Client_a/New Documents.txt")
        };

        var summary = DirectUploadIssueUtilities.BuildSkippedFilesSummary(issues);

        Assert.Contains("Skipped (1)", summary);
        Assert.Contains("Client_a/New Documents.txt", summary);
    }

    [Fact]
    public void BuildSkippedFilesSummary_TruncatesWhenMoreThanMaxInline()
    {
        var issues = new[]
        {
            new DirectUploadIssue(DirectUploadIssueSeverity.Info, "ZERO_BYTE_FILE_SKIPPED", "skip", "A/one.txt"),
            new DirectUploadIssue(DirectUploadIssueSeverity.Info, "MISSING_FILE_SKIPPED", "skip", "B/two.txt"),
            new DirectUploadIssue(DirectUploadIssueSeverity.Info, "ZERO_BYTE_FILE_SKIPPED", "skip", "C/three.txt"),
            new DirectUploadIssue(DirectUploadIssueSeverity.Info, "MISSING_FILE_SKIPPED", "skip", "D/four.txt")
        };

        var summary = DirectUploadIssueUtilities.BuildSkippedFilesSummary(issues, maxInline: 3);

        Assert.Contains("Skipped (4)", summary);
        Assert.Contains("A/one.txt", summary);
        Assert.Contains("B/two.txt", summary);
        Assert.Contains("C/three.txt", summary);
        Assert.Contains("(+1 more)", summary);
    }

    [Fact]
    public void BuildSkippedFilesSummary_ReturnsEmpty_WhenNoSkippedIssues()
    {
        var issues = new[]
        {
            new DirectUploadIssue(DirectUploadIssueSeverity.Error, "REQUIRED_PROFILE_MISSING", "missing", "Client_a/Doc1.pdf")
        };

        var summary = DirectUploadIssueUtilities.BuildSkippedFilesSummary(issues);

        Assert.Equal(string.Empty, summary);
    }

    [Fact]
    public void IsSkippedFileIssue_ReturnsTrue_ForSkipCodes()
    {
        var zero = new DirectUploadIssue(DirectUploadIssueSeverity.Info, "ZERO_BYTE_FILE_SKIPPED", "skip", "x");
        var missing = new DirectUploadIssue(DirectUploadIssueSeverity.Info, "MISSING_FILE_SKIPPED", "skip", "y");
        var other = new DirectUploadIssue(DirectUploadIssueSeverity.Info, "ACL_REQUIRED", "nope", "z");

        Assert.True(DirectUploadIssueUtilities.IsSkippedFileIssue(zero));
        Assert.True(DirectUploadIssueUtilities.IsSkippedFileIssue(missing));
        Assert.False(DirectUploadIssueUtilities.IsSkippedFileIssue(other));
    }
}
