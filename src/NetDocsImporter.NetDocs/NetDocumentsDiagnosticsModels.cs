namespace NetDocsImporter.NetDocs;

public sealed class NdApiCallTrace
{
    public int Sequence { get; set; }

    public string Method { get; set; } = string.Empty;

    public string RelativePath { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public int StatusCode { get; set; }

    public bool Succeeded { get; set; }

    public long DurationMs { get; set; }

    public int ResponseLength { get; set; }

    public string ResponsePreview { get; set; } = string.Empty;

    public string ErrorMessage { get; set; } = string.Empty;

    public decimal? ApiCost { get; set; }

    public decimal? ApiRemaining { get; set; }

    public decimal? ApiSpendSoFar { get; set; }

    public decimal? ApiTotalAvailable { get; set; }

    public string ApiUsageSource { get; set; } = string.Empty;
}

public sealed class NdApiDuplicateEndpointSummary
{
    public string Method { get; set; } = string.Empty;

    public string RelativePath { get; set; } = string.Empty;

    public int Count { get; set; }

    public int SucceededCount { get; set; }

    public int FailedCount { get; set; }

    public long TotalDurationMs { get; set; }

    public decimal? LatestSpendSoFar { get; set; }

    public decimal? LatestTotalAvailable { get; set; }
}

public sealed class NdApiRunSummary
{
    public int TotalCalls { get; set; }

    public int DistinctEndpoints { get; set; }

    public int DuplicateEndpointCount { get; set; }

    public int DuplicateCalls { get; set; }

    public decimal? LatestSpendSoFar { get; set; }

    public decimal? LatestTotalAvailable { get; set; }

    public List<NdApiDuplicateEndpointSummary> DuplicateEndpoints { get; set; } = new();
}

public static class NdApiTraceSummaryBuilder
{
    public static NdApiRunSummary Build(IEnumerable<NdApiCallTrace>? traces)
    {
        var items = traces?
            .Where(trace => trace is not null && !string.IsNullOrWhiteSpace(trace.Method) && !string.IsNullOrWhiteSpace(trace.RelativePath))
            .ToList() ?? [];

        if (items.Count == 0)
        {
            return new NdApiRunSummary();
        }

        var duplicates = items
            .GroupBy(trace => $"{trace.Method}\n{trace.RelativePath}", StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var latestBySequence = group
                    .OrderByDescending(trace => trace.Sequence)
                    .ThenByDescending(trace => trace.DurationMs)
                    .First();
                return new NdApiDuplicateEndpointSummary
                {
                    Method = latestBySequence.Method,
                    RelativePath = latestBySequence.RelativePath,
                    Count = group.Count(),
                    SucceededCount = group.Count(trace => trace.Succeeded),
                    FailedCount = group.Count(trace => !trace.Succeeded),
                    TotalDurationMs = group.Sum(trace => trace.DurationMs),
                    LatestSpendSoFar = latestBySequence.ApiSpendSoFar,
                    LatestTotalAvailable = latestBySequence.ApiTotalAvailable
                };
            })
            .Where(summary => summary.Count > 1)
            .OrderByDescending(summary => summary.Count)
            .ThenByDescending(summary => summary.TotalDurationMs)
            .ThenBy(summary => summary.Method, StringComparer.OrdinalIgnoreCase)
            .ThenBy(summary => summary.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var latestUsage = items
            .OrderByDescending(trace => trace.Sequence)
            .ThenByDescending(trace => trace.DurationMs)
            .FirstOrDefault(trace => trace.ApiSpendSoFar.HasValue || trace.ApiTotalAvailable.HasValue);

        return new NdApiRunSummary
        {
            TotalCalls = items.Count,
            DistinctEndpoints = items
                .Select(trace => $"{trace.Method}\n{trace.RelativePath}")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(),
            DuplicateEndpointCount = duplicates.Count,
            DuplicateCalls = duplicates.Sum(summary => summary.Count - 1),
            LatestSpendSoFar = latestUsage?.ApiSpendSoFar,
            LatestTotalAvailable = latestUsage?.ApiTotalAvailable,
            DuplicateEndpoints = duplicates
        };
    }
}

public static class NdApiRunSummaryFormatter
{
    public static IReadOnlyList<string> BuildLogSectionLines(NdApiRunSummary? summary, int maxDuplicateEndpoints = 10)
    {
        var effective = summary ?? new NdApiRunSummary();
        var lines = new List<string>
        {
            "+------------------------------------------------------------+",
            "| API Duplicate Summary                                      |",
            "+------------------------------------------------------------+",
            $" TotalCalls: {effective.TotalCalls:N0}",
            $" DistinctEndpoints: {effective.DistinctEndpoints:N0}",
            $" DuplicateEndpoints: {effective.DuplicateEndpointCount:N0}",
            $" DuplicateCalls: {effective.DuplicateCalls:N0}",
            $" SpendSoFar: {FormatApiMetric(effective.LatestSpendSoFar)}",
            $" TotalAvailable: {FormatApiMetric(effective.LatestTotalAvailable)}"
        };

        if (effective.DuplicateEndpoints.Count > 0)
        {
            lines.Add(" Top duplicates:");
            foreach (var duplicate in effective.DuplicateEndpoints.Take(Math.Max(0, maxDuplicateEndpoints)))
            {
                lines.Add(
                    $"  [{duplicate.Count}x] {duplicate.Method} {duplicate.RelativePath} success={duplicate.SucceededCount} fail={duplicate.FailedCount} totalMs={duplicate.TotalDurationMs}");
            }
        }
        else
        {
            lines.Add(" No duplicate endpoints detected.");
        }

        return lines;
    }

    private static string FormatApiMetric(decimal? value)
    {
        return value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }
}

public sealed class NdWorkspaceLoadComparisonStrategyResult
{
    public string Name { get; set; } = string.Empty;

    public bool Succeeded { get; set; }

    public string ErrorMessage { get; set; } = string.Empty;

    public long DurationMs { get; set; }

    public int ContainerCount { get; set; }

    public int SummaryRowCount { get; set; }

    public int DocumentCount { get; set; }

    public List<NdApiCallTrace> ApiCalls { get; set; } = new();
}

public sealed class NdWorkspaceLoadComparisonResult
{
    public string CabinetId { get; set; } = string.Empty;

    public string WorkspaceId { get; set; } = string.Empty;

    public DateTime StartedUtc { get; set; }

    public DateTime CompletedUtc { get; set; }

    public NdWorkspaceLoadComparisonStrategyResult CurrentStrategy { get; set; } = new();

    public NdWorkspaceLoadComparisonStrategyResult UiLikeMetadataOnlyStrategy { get; set; } = new();

    public NdWorkspaceLoadComparisonStrategyResult UiLikeStrategy { get; set; } = new();

    public NdWorkspaceLoadComparisonStrategyResult UiLikeParallelStrategy { get; set; } = new();
}
