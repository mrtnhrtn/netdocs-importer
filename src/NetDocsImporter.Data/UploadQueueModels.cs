namespace NetDocsImporter.Data;

public enum UploadQueueJobState
{
    Scheduled,
    Queued,
    Running,
    Completed,
    Failed,
    Canceled
}

public sealed record UploadQueueJobRecord(
    string QueueJobId,
    DateTime CreatedUtc,
    DateTime? ScheduledForUtc,
    UploadQueueJobState State,
    string SourceJobId,
    string SourceRoot,
    string SnapshotJson,
    DateTime? StartedUtc = null,
    DateTime? FinishedUtc = null,
    string? Error = null);

public interface IUploadQueueStore
{
    Task<int> PromoteDueScheduledJobsAsync(DateTime utcNow, CancellationToken cancellationToken = default);

    Task<int> FailRunningJobsAsync(DateTime utcNow, string reason, CancellationToken cancellationToken = default);

    Task<UploadQueueJobRecord?> TryAcquireNextQueuedJobAsync(DateTime utcNow, CancellationToken cancellationToken = default);

    Task MarkJobCompletedAsync(string queueJobId, DateTime utcNow, CancellationToken cancellationToken = default);

    Task MarkJobFailedAsync(string queueJobId, DateTime utcNow, string error, CancellationToken cancellationToken = default);

    Task<UploadQueueJobRecord?> GetRunningJobAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<UploadQueueJobRecord>> GetQueueViewAsync(int take, CancellationToken cancellationToken = default);
}
