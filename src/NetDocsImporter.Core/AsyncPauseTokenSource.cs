namespace NetDocsImporter.Core;

public sealed class AsyncPauseTokenSource
{
    private volatile TaskCompletionSource<bool> _resumeSource =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public AsyncPauseTokenSource()
    {
        _resumeSource.SetResult(true);
    }

    public bool IsPaused => !_resumeSource.Task.IsCompleted;

    public void Pause()
    {
        if (IsPaused)
        {
            return;
        }

        _resumeSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Resume()
    {
        _resumeSource.TrySetResult(true);
    }

    public Task WaitWhilePausedAsync(CancellationToken cancellationToken)
    {
        return _resumeSource.Task.WaitAsync(cancellationToken);
    }
}
