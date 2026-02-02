using NetDocsImporter.Data;

namespace NetDocsImporter.Core;

public sealed class ScanJobRunner
{
    private readonly JobStore _jobStore;

    public ScanJobRunner(JobStore jobStore)
    {
        _jobStore = jobStore ?? throw new ArgumentNullException(nameof(jobStore));
    }

    public async Task RunAsync(
        string sourceRoot,
        long largeFileThresholdBytes,
        IProgress<FileScanProgress>? progress,
        CancellationToken cancellationToken,
        string? jobId = null)
    {
        if (string.IsNullOrWhiteSpace(sourceRoot))
        {
            throw new ArgumentException("Source root is required.", nameof(sourceRoot));
        }

        var resolvedJobId = string.IsNullOrWhiteSpace(jobId) ? Guid.NewGuid().ToString("N") : jobId;

        await _jobStore.InitializeAsync(cancellationToken);
        await _jobStore.InsertJobAsync(new JobRecord(
            resolvedJobId,
            DateTime.UtcNow,
            sourceRoot,
            "Scanning"), cancellationToken);

        JobFileWriter? writer = null;

        try
        {
            writer = _jobStore.OpenFileWriter();
            await FileScanner.ScanAsync(
                sourceRoot,
                largeFileThresholdBytes,
                progress,
                cancellationToken,
                item =>
                {
                    var record = new FileRecord(
                        Guid.NewGuid().ToString("N"),
                        resolvedJobId,
                        item.FullPath,
                        item.RelativePath,
                        item.SizeBytes,
                        item.ModifiedUtc,
                        item.IsLargeWarning);
                    writer.Insert(record);
                });

            await _jobStore.UpdateJobStatusAsync(resolvedJobId, "Complete", cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await _jobStore.UpdateJobStatusAsync(resolvedJobId, "Canceled", cancellationToken);
            throw;
        }
        catch
        {
            await _jobStore.UpdateJobStatusAsync(resolvedJobId, "Failed", cancellationToken);
            throw;
        }
        finally
        {
            writer?.Dispose();
        }
    }
}
