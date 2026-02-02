namespace NetDocsImporter.Core;

public interface IClock
{
    DateTime UtcNow { get; }

    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}
