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
