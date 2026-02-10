using System.Globalization;

namespace NetDocsImporter.Core;

public static class DirectUploadIssueUtilities
{
    public static bool IsSkippedFileIssue(DirectUploadIssue issue)
    {
        return string.Equals(issue.Code, "ZERO_BYTE_FILE_SKIPPED", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(issue.Code, "MISSING_FILE_SKIPPED", StringComparison.OrdinalIgnoreCase);
    }

    public static string BuildSkippedFilesSummary(IReadOnlyList<DirectUploadIssue> issues, int maxInline = 3)
    {
        if (issues is null || issues.Count == 0)
        {
            return string.Empty;
        }

        var maxItems = Math.Max(1, maxInline);
        var skippedPaths = issues
            .Where(IsSkippedFileIssue)
            .Select(i => i.RelativePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (skippedPaths.Count == 0)
        {
            return string.Empty;
        }

        var inlinePaths = skippedPaths.Take(maxItems).ToList();
        var summary = $"Skipped ({skippedPaths.Count.ToString("N0", CultureInfo.CurrentCulture)}): {string.Join("; ", inlinePaths)}";
        if (skippedPaths.Count > inlinePaths.Count)
        {
            summary += $" (+{(skippedPaths.Count - inlinePaths.Count).ToString("N0", CultureInfo.CurrentCulture)} more)";
        }

        return summary;
    }
}
