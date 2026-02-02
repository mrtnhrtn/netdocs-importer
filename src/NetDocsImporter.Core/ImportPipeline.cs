using System.Diagnostics;
using System.Threading.Channels;
using NetDocsImporter.Data;

namespace NetDocsImporter.Core;

public sealed class ImportPipeline
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromMilliseconds(1500)
    ];

    private readonly JobStore _jobStore;
    private readonly ISinkUploader _uploader;
    private readonly IClock _clock;
    private readonly IPipelineLogger _logger;
    private readonly AsyncPauseTokenSource _pauseSource = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private DateTime _nextAllowedStartUtc = DateTime.MinValue;

    public ImportPipeline(
        JobStore jobStore,
        ISinkUploader uploader,
        IClock? clock = null,
        IPipelineLogger? logger = null)
    {
        _jobStore = jobStore ?? throw new ArgumentNullException(nameof(jobStore));
        _uploader = uploader ?? throw new ArgumentNullException(nameof(uploader));
        _clock = clock ?? new SystemClock();
        _logger = logger ?? new NullPipelineLogger();
    }

    public bool IsPaused => _pauseSource.IsPaused;

    public void Pause() => _pauseSource.Pause();

    public void Resume() => _pauseSource.Resume();

    public async Task RunAsync(
        string jobId,
        int maxConcurrency,
        int msDelayBetweenStarts,
        IProgress<TransferUpdate>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            throw new ArgumentException("Job id is required.", nameof(jobId));
        }

        if (maxConcurrency <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrency));
        }

        if (msDelayBetweenStarts < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(msDelayBetweenStarts));
        }

        await _jobStore.InitializeAsync(cancellationToken);

        var files = await _jobStore.GetIncludedFilesForJobAsync(jobId, cancellationToken);
        var transferStates = await _jobStore.GetTransferStatesByFileAsync(jobId, cancellationToken);

        var channel = Channel.CreateBounded<TransferWorkItem>(new BoundedChannelOptions(maxConcurrency * 2)
        {
            FullMode = BoundedChannelFullMode.Wait
        });

        var writer = channel.Writer;
        var reader = channel.Reader;

        var queueTask = Task.Run(async () =>
        {
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (transferStates.TryGetValue(file.FileId, out var state) &&
                    string.Equals(state.Status, "Succeeded", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var transferId = transferStates.TryGetValue(file.FileId, out var existing)
                    ? existing.TransferId
                    : Guid.NewGuid().ToString("N");

                var attempt = transferStates.TryGetValue(file.FileId, out var existingState)
                    ? Math.Min(existingState.Attempt + 1, MaxAttempts)
                    : 1;

                var transfer = new TransferRecord(
                    transferId,
                    jobId,
                    file.FileId,
                    attempt,
                    "Queued",
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null);

                await _jobStore.UpsertTransferQueuedAsync(transfer, cancellationToken);
                progress?.Report(new TransferUpdate(transferId, file.FileId, "Queued", attempt, null, null, null, null));

                await writer.WriteAsync(new TransferWorkItem(file, transferId, attempt), cancellationToken);
            }

            writer.TryComplete();
        }, cancellationToken);

        var workers = Enumerable.Range(0, maxConcurrency)
            .Select(workerId => Task.Run(() => WorkerLoop(workerId, jobId, reader, msDelayBetweenStarts, progress, cancellationToken), cancellationToken))
            .ToArray();

        try
        {
            await Task.WhenAll(workers.Concat([queueTask]));
        }
        catch (OperationCanceledException)
        {
            await _jobStore.MarkQueuedTransfersCanceledAsync(jobId, CancellationToken.None);
            throw;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            await _jobStore.MarkQueuedTransfersCanceledAsync(jobId, CancellationToken.None);
        }
    }

    private async Task WorkerLoop(
        int workerId,
        string jobId,
        ChannelReader<TransferWorkItem> reader,
        int msDelayBetweenStarts,
        IProgress<TransferUpdate>? progress,
        CancellationToken cancellationToken)
    {
        await foreach (var item in reader.ReadAllAsync(cancellationToken))
        {
            var attempt = item.Attempt;
            while (attempt <= MaxAttempts)
            {
                await _pauseSource.WaitWhilePausedAsync(cancellationToken);
                await WaitForStartGateAsync(msDelayBetweenStarts, cancellationToken);

                var startedUtc = _clock.UtcNow;
                await _jobStore.UpdateTransferRunningAsync(item.TransferId, attempt, startedUtc, workerId, cancellationToken);
                progress?.Report(new TransferUpdate(item.TransferId, item.File.FileId, "Running", attempt, startedUtc, null, null, null));
                _logger.Info("Transfer started", new Dictionary<string, object?>
                {
                    ["transferId"] = item.TransferId,
                    ["fileId"] = item.File.FileId,
                    ["attempt"] = attempt,
                    ["workerId"] = workerId
                });

                var stopwatch = Stopwatch.StartNew();
                UploadResult result;
                try
                {
                    result = await _uploader.UploadAsync(item.File, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    await FinishCanceledAsync(item.TransferId, item.File.FileId, attempt, startedUtc, stopwatch, progress, cancellationToken);
                    return;
                }
                catch (Exception ex)
                {
                    result = new UploadResult(false, 500, "Unhandled exception.", ex.Message, 0);
                }
                finally
                {
                    stopwatch.Stop();
                }

                var finishedUtc = _clock.UtcNow;
                var status = result.Succeeded ? "Succeeded" : "Failed";
                var error = result.Succeeded ? null : Truncate(result.Error ?? "Unknown error.", 2048);
                var snippet = Truncate(result.ResponseSnippet, 2048);

                await _jobStore.UpdateTransferFinishedAsync(
                    item.TransferId,
                    status,
                    finishedUtc,
                    stopwatch.ElapsedMilliseconds,
                    error,
                    result.HttpStatus,
                    snippet,
                    result.SimulatedDelayMs,
                    cancellationToken);

                progress?.Report(new TransferUpdate(
                    item.TransferId,
                    item.File.FileId,
                    status,
                    attempt,
                    startedUtc,
                    finishedUtc,
                    stopwatch.ElapsedMilliseconds,
                    error));

                _logger.Info("Transfer finished", new Dictionary<string, object?>
                {
                    ["transferId"] = item.TransferId,
                    ["fileId"] = item.File.FileId,
                    ["attempt"] = attempt,
                    ["status"] = status
                });

                if (result.Succeeded)
                {
                    break;
                }

                attempt++;
                if (attempt > MaxAttempts)
                {
                    break;
                }

                var delayIndex = attempt - 2;
                if (delayIndex >= 0 && delayIndex < RetryDelays.Length)
                {
                    await _clock.DelayAsync(RetryDelays[delayIndex], cancellationToken);
                }

                await _jobStore.UpsertTransferQueuedAsync(new TransferRecord(
                    item.TransferId,
                    jobId,
                    item.File.FileId,
                    attempt,
                    "Queued",
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null), cancellationToken);

                progress?.Report(new TransferUpdate(item.TransferId, item.File.FileId, "Queued", attempt, null, null, null, null));
            }
        }
    }

    private async Task FinishCanceledAsync(
        string transferId,
        string fileId,
        int attempt,
        DateTime startedUtc,
        Stopwatch stopwatch,
        IProgress<TransferUpdate>? progress,
        CancellationToken cancellationToken)
    {
        var finishedUtc = _clock.UtcNow;
        await _jobStore.UpdateTransferFinishedAsync(
            transferId,
            "Canceled",
            finishedUtc,
            stopwatch.ElapsedMilliseconds,
            "Canceled",
            null,
            null,
            null,
            cancellationToken);

        progress?.Report(new TransferUpdate(
            transferId,
            fileId,
            "Canceled",
            attempt,
            startedUtc,
            finishedUtc,
            stopwatch.ElapsedMilliseconds,
            "Canceled"));
    }

    private async Task WaitForStartGateAsync(int msDelayBetweenStarts, CancellationToken cancellationToken)
    {
        await _startGate.WaitAsync(cancellationToken);
        try
        {
            var now = _clock.UtcNow;
            if (now < _nextAllowedStartUtc)
            {
                await _clock.DelayAsync(_nextAllowedStartUtc - now, cancellationToken);
            }

            _nextAllowedStartUtc = _clock.UtcNow.AddMilliseconds(msDelayBetweenStarts);
        }
        finally
        {
            _startGate.Release();
        }
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength];
    }

    private sealed record TransferWorkItem(FileRecord File, string TransferId, int Attempt);
}

public sealed record TransferUpdate(
    string TransferId,
    string FileId,
    string Status,
    int Attempt,
    DateTime? StartedUtc,
    DateTime? FinishedUtc,
    long? DurationMs,
    string? Error);
