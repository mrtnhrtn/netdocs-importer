using System.Text.Json;

namespace NetDocsImporter.Core;

public sealed class UploadQueueSnapshot
{
    public string SourceJobId { get; init; } = string.Empty;

    public string SourceRoot { get; init; } = string.Empty;

    public string RepositoryId { get; init; } = string.Empty;

    public string CabinetId { get; init; } = string.Empty;

    public string TargetDisplayName { get; init; } = string.Empty;

    public NdTargetSelection Target { get; init; } = new();

    public DirectUploadPlanContext PlanContext { get; init; } = new();

    public DateTime CapturedUtc { get; init; }

    public string ToJson()
    {
        return JsonSerializer.Serialize(this);
    }

    public static UploadQueueSnapshot? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<UploadQueueSnapshot>(json);
        }
        catch
        {
            return null;
        }
    }
}
