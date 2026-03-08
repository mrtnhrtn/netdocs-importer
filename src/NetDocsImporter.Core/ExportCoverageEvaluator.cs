namespace NetDocsImporter.Core;

public sealed class ExportAllVersionsCoverageAssessment
{
    public bool HasBlockingIssue { get; init; }

    public int UnknownCoverageDocumentCount { get; init; }

    public int MissingExactVersionIdsDocumentCount { get; init; }

    public string Message { get; init; } = string.Empty;
}

public static class ExportCoverageEvaluator
{
    public static ExportAllVersionsCoverageAssessment AssessAllVersionsCoverage(
        IReadOnlyList<string> documentsWithUnknownVersionCoverage,
        IReadOnlyList<string> documentsMissingExactVersionIds,
        int sampleSize = 3)
    {
        if (documentsWithUnknownVersionCoverage is null)
        {
            throw new ArgumentNullException(nameof(documentsWithUnknownVersionCoverage));
        }

        if (documentsMissingExactVersionIds is null)
        {
            throw new ArgumentNullException(nameof(documentsMissingExactVersionIds));
        }

        var unknownCoverageCount = documentsWithUnknownVersionCoverage.Count;
        var missingExactVersionIdsCount = documentsMissingExactVersionIds.Count;
        if (unknownCoverageCount == 0 && missingExactVersionIdsCount == 0)
        {
            return new ExportAllVersionsCoverageAssessment();
        }

        var examples = documentsMissingExactVersionIds
            .Concat(documentsWithUnknownVersionCoverage)
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, sampleSize))
            .ToList();
        var exampleSuffix = examples.Count == 0
            ? string.Empty
            : $" Example: {string.Join(", ", examples)}.";
        var details = new List<string>();
        if (missingExactVersionIdsCount > 0)
        {
            details.Add($"{missingExactVersionIdsCount:N0} document(s) reported multiple versions, but exact version ids were not returned");
        }

        if (unknownCoverageCount > 0)
        {
            details.Add($"{unknownCoverageCount:N0} document(s) did not return enough `VersionsLite` detail to prove whether additional versions exist");
        }

        return new ExportAllVersionsCoverageAssessment
        {
            HasBlockingIssue = true,
            UnknownCoverageDocumentCount = unknownCoverageCount,
            MissingExactVersionIdsDocumentCount = missingExactVersionIdsCount,
            Message =
                $"All versions export requires exact version enumeration. {string.Join("; ", details)}.{exampleSuffix} Run version expansion to continue, or turn off All versions."
        };
    }
}
