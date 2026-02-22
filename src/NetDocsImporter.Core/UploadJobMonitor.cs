using NetDocsImporter.Data;

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
        try
        {
            await _store.PromoteDueScheduledJobsAsync(_clock.UtcNow, cancellationToken);
            var acquired = await _store.TryAcquireNextQueuedJobAsync(_clock.UtcNow, cancellationToken);
            if (acquired is null)
            {
                return;
            }

            UploadRunnerResult result;
            try
            {
                result = await _runner.RunAsync(acquired, cancellationToken);
            }
            catch (Exception ex)
            {
                result = new UploadRunnerResult(false, ex.Message);
            }

            if (result.Succeeded)
            {
                await _store.MarkJobCompletedAsync(acquired.QueueJobId, _clock.UtcNow, cancellationToken);
            }
            else
            {
                await _store.MarkJobFailedAsync(
                    acquired.QueueJobId,
                    _clock.UtcNow,
                    result.Error ?? "Upload runner reported failure.",
                    cancellationToken);
            }
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
            catch
            {
                // Keep monitor alive; next poll can recover.
            }

            await _clock.DelayAsync(_pollInterval, cancellationToken);
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
