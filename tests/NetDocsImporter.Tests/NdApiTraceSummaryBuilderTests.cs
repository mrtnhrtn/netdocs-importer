using NetDocsImporter.NetDocs;

namespace NetDocsImporter.Tests;

public sealed class NdApiTraceSummaryBuilderTests
{
    [Fact]
    public void Build_AggregatesDuplicateEndpoints_AndLatestUsage()
    {
        var summary = NdApiTraceSummaryBuilder.Build(
        [
            new NdApiCallTrace
            {
                Sequence = 1,
                Method = "GET",
                RelativePath = "/v2/container/FLD-1/sub?recursive=false&max=200&listflags=ValidateWorkspaces",
                Succeeded = true,
                DurationMs = 40
            },
            new NdApiCallTrace
            {
                Sequence = 2,
                Method = "GET",
                RelativePath = "/v2/container/FLD-1/sub?recursive=false&max=200&listflags=ValidateWorkspaces",
                Succeeded = false,
                DurationMs = 15,
                ApiSpendSoFar = 25,
                ApiTotalAvailable = 1000
            },
            new NdApiCallTrace
            {
                Sequence = 3,
                Method = "POST",
                RelativePath = "/v1/upload",
                Succeeded = true,
                DurationMs = 55
            }
        ]);

        Assert.Equal(3, summary.TotalCalls);
        Assert.Equal(2, summary.DistinctEndpoints);
        Assert.Equal(1, summary.DuplicateEndpointCount);
        Assert.Equal(1, summary.DuplicateCalls);
        Assert.Equal(25m, summary.LatestSpendSoFar);
        Assert.Equal(1000m, summary.LatestTotalAvailable);

        var duplicate = Assert.Single(summary.DuplicateEndpoints);
        Assert.Equal("GET", duplicate.Method);
        Assert.Equal(2, duplicate.Count);
        Assert.Equal(1, duplicate.SucceededCount);
        Assert.Equal(1, duplicate.FailedCount);
        Assert.Equal(55, duplicate.TotalDurationMs);
        Assert.Equal(25m, duplicate.LatestSpendSoFar);
        Assert.Equal(1000m, duplicate.LatestTotalAvailable);
    }

    [Fact]
    public void BuildLogSectionLines_FormatsDuplicateSummaryForRunLogs()
    {
        var lines = NdApiRunSummaryFormatter.BuildLogSectionLines(new NdApiRunSummary
        {
            TotalCalls = 8,
            DistinctEndpoints = 5,
            DuplicateEndpointCount = 2,
            DuplicateCalls = 3,
            LatestSpendSoFar = 41,
            LatestTotalAvailable = 1000,
            DuplicateEndpoints =
            [
                new NdApiDuplicateEndpointSummary
                {
                    Method = "GET",
                    RelativePath = "/v2/container/WS-1?top=200",
                    Count = 3,
                    SucceededCount = 3,
                    FailedCount = 0,
                    TotalDurationMs = 180
                }
            ]
        });

        Assert.Contains(" TotalCalls: 8", lines);
        Assert.Contains(" DuplicateCalls: 3", lines);
        Assert.Contains(" SpendSoFar: 41", lines);
        Assert.Contains(" TotalAvailable: 1000", lines);
        Assert.Contains("  [3x] GET /v2/container/WS-1?top=200 success=3 fail=0 totalMs=180", lines);
    }
}
