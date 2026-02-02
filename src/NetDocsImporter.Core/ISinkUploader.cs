using NetDocsImporter.Data;

namespace NetDocsImporter.Core;

public interface ISinkUploader
{
    Task<UploadResult> UploadAsync(FileRecord file, CancellationToken cancellationToken);
}

public sealed record UploadResult(
    bool Succeeded,
    int HttpStatus,
    string ResponseSnippet,
    string? Error,
    int SimulatedDelayMs);
