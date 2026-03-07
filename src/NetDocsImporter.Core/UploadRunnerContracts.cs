using NetDocsImporter.Data;

namespace NetDocsImporter.Core;

public sealed record UploadRunnerResult(bool Succeeded, string? Error = null);

public interface IUploadRunner
{
    Task<UploadRunnerResult> RunAsync(UploadQueueJobRecord job, CancellationToken cancellationToken = default);
}
