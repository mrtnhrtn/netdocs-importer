using NetDocsImporter.Data;

namespace NetDocsImporter.Core;

public sealed class DryRunUploader : ISinkUploader
{
    private readonly Random _random;
    private readonly IClock _clock;
    private readonly object _lock = new();

    public DryRunUploader(Random? random = null, IClock? clock = null)
    {
        _random = random ?? new Random();
        _clock = clock ?? new SystemClock();
    }

    public async Task<UploadResult> UploadAsync(FileRecord file, CancellationToken cancellationToken)
    {
        int delayMs;
        double roll;
        lock (_lock)
        {
            delayMs = _random.Next(200, 1201);
            roll = _random.NextDouble();
        }

        await _clock.DelayAsync(TimeSpan.FromMilliseconds(delayMs), cancellationToken);

        if (roll < 0.02)
        {
            return new UploadResult(
                false,
                500,
                "Simulated failure.",
                $"Synthetic error for {file.RelativePath}",
                delayMs);
        }

        return new UploadResult(
            true,
            200,
            "Simulated success.",
            null,
            delayMs);
    }
}
