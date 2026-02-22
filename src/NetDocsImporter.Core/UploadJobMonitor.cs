using NetDocsImporter.Data;
using System.Diagnostics;

namespace NetDocsImporter.Core;

public sealed class UploadJobMonitor : IAsyncDisposable
{
    private readonly IUploadQueueStore _store;
    private readonly IUploadRunner _runner;
    private readonly IClock _clock;
    private readonly TimeSpan _pollInterval;
    private readonly SemaphoreSlim _tickGate = new(1, 1);
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;
    private int _startupPromotedDueCount;

    public UploadJobMonitor(
        IUploadQueueStore store,
        IUploadRunner runner,
        IClock clock,
        TimeSpan? pollInterval = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _pollInterval = pollInterval.GetValueOrDefault(TimeSpan.FromSeconds(5));
    }

    public int StartupPromotedDueCount => _startupPromotedDueCount;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var promotedDue = await _store.PromoteDueScheduledJobsAsync(_clock.UtcNow, cancellationToken);
        var failedRunning = await _store.FailRunningJobsAsync(
            _clock.UtcNow,
            "Interrupted when the app closed before completion.",
            cancellationToken);
        _startupPromotedDueCount = promotedDue + failedRunning;

        _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runTask = RunLoopAsync(_runCancellation.Token);
    }

    public async Task TickOnceAsync(CancellationToken cancellationToken = default)
    {
        await _tickGate.WaitAsync(cancellationToken);
        string stage = "promote-due";
        string? queueJobId = null;
        try
        {
            await _store.PromoteDueScheduledJobsAsync(_clock.UtcNow, cancellationToken);
            stage = "acquire";
            var acquired = await _store.TryAcquireNextQueuedJobAsync(_clock.UtcNow, cancellationToken);
            if (acquired is null)
            {
                return;
            }

            queueJobId = acquired.QueueJobId;
            stage = "run";
            UploadRunnerResult result;
            try
            {
                result = await _runner.RunAsync(acquired, cancellationToken);
            }
            catch (Exception ex)
            {
                Trace.WriteLine(
                    $"QUEUE-MONITOR runner exception queueJobId='{queueJobId}' stage='{stage}' error='{ex.Message}'.");
                result = new UploadRunnerResult(false, ex.Message);
            }

            if (result.Succeeded)
            {
                stage = "mark-completed";
                await _store.MarkJobCompletedAsync(acquired.QueueJobId, _clock.UtcNow, cancellationToken);
                Trace.WriteLine($"QUEUE-MONITOR completed queueJobId='{queueJobId}'.");
            }
            else
            {
                stage = "mark-failed";
                await _store.MarkJobFailedAsync(
                    acquired.QueueJobId,
                    _clock.UtcNow,
                    result.Error ?? "Upload runner reported failure.",
                    cancellationToken);
                Trace.WriteLine(
                    $"QUEUE-MONITOR failed queueJobId='{queueJobId}' error='{result.Error ?? "Upload runner reported failure."}'.");
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Trace.WriteLine(
                $"QUEUE-MONITOR tick exception stage='{stage}' queueJobId='{queueJobId ?? string.Empty}' error='{ex.Message}'.");
            if (!string.IsNullOrWhiteSpace(queueJobId))
            {
                await TryMarkCurrentJobFailedAsync(queueJobId, stage, ex, cancellationToken);
            }

            throw;
        }
        finally
        {
            _tickGate.Release();
        }
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await TickOnceAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"QUEUE-MONITOR loop exception error='{ex.Message}'.");
                await TryRecoveryTickAsync(cancellationToken);
            }

            await _clock.DelayAsync(_pollInterval, cancellationToken);
        }
    }

    private async Task TryRecoveryTickAsync(CancellationToken cancellationToken)
    {
        try
        {
            var promoted = await _store.PromoteDueScheduledJobsAsync(_clock.UtcNow, cancellationToken);
            Trace.WriteLine($"QUEUE-MONITOR recovery tick promotedDue={promoted}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Ignore on shutdown.
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"QUEUE-MONITOR recovery failed error='{ex.Message}'.");
        }
    }

    private async Task TryMarkCurrentJobFailedAsync(
        string queueJobId,
        string stage,
        Exception exception,
        CancellationToken cancellationToken)
    {
        try
        {
            await _store.MarkJobFailedAsync(
                queueJobId,
                _clock.UtcNow,
                $"Queue monitor exception during '{stage}': {exception.Message}",
                cancellationToken);
            Trace.WriteLine(
                $"QUEUE-MONITOR recovered by failing queueJobId='{queueJobId}' stage='{stage}'.");
        }
        catch (Exception markEx) when (!cancellationToken.IsCancellationRequested)
        {
            Trace.WriteLine(
                $"QUEUE-MONITOR failed to recover queueJobId='{queueJobId}' stage='{stage}' error='{markEx.Message}'.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_runCancellation is null)
        {
            return;
        }

        _runCancellation.Cancel();
        if (_runTask is not null)
        {
            try
            {
                await _runTask;
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

        _runCancellation.Dispose();
        _runCancellation = null;
        _runTask = null;
    }
}
