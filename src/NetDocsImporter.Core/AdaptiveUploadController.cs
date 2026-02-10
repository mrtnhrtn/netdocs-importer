using System.Diagnostics;

namespace NetDocsImporter.Core;

public sealed class AdaptiveUploadController
{
    private readonly int _minConcurrency;
    private readonly int _maxConcurrency;
    private readonly int _successesForScaleUp;
    private readonly object _sync = new();
    private readonly Random _random = new();
    private int _successStreak;
    private int _backoffMs;
    private int _currentConcurrency;
    private DateTime _blockedUntilUtc = DateTime.MinValue;

    public AdaptiveUploadController(
        int minConcurrency,
        int maxConcurrency,
        int initialConcurrency,
        int successesForScaleUp = 20)
    {
        if (minConcurrency <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minConcurrency));
        }

        if (maxConcurrency < minConcurrency)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrency));
        }

        _minConcurrency = minConcurrency;
        _maxConcurrency = maxConcurrency;
        _successesForScaleUp = Math.Max(1, successesForScaleUp);
        _currentConcurrency = Math.Clamp(initialConcurrency, minConcurrency, maxConcurrency);
    }

    public int CurrentConcurrency => Volatile.Read(ref _currentConcurrency);

    public int CurrentBackoffMs
    {
        get
        {
            lock (_sync)
            {
                return _backoffMs;
            }
        }
    }

    public async Task WaitForSlotAsync(int workerId, CancellationToken cancellationToken)
    {
        while (workerId >= Volatile.Read(ref _currentConcurrency))
        {
            await Task.Delay(125, cancellationToken);
        }

        TimeSpan extraDelay;
        lock (_sync)
        {
            var now = DateTime.UtcNow;
            var blockedDelay = _blockedUntilUtc > now ? _blockedUntilUtc - now : TimeSpan.Zero;
            if (_backoffMs <= 0)
            {
                extraDelay = blockedDelay;
            }
            else
            {
                // +/-15% jitter to avoid synchronized bursts.
                var jitter = _random.Next((int)(_backoffMs * 0.85), Math.Max((int)(_backoffMs * 1.15), _backoffMs + 1));
                extraDelay = blockedDelay + TimeSpan.FromMilliseconds(jitter);
            }
        }

        if (extraDelay > TimeSpan.Zero)
        {
            await Task.Delay(extraDelay, cancellationToken);
        }
    }

    public TimeSpan RegisterOutcome(int httpStatus, bool succeeded, TimeSpan? retryAfter = null)
    {
        lock (_sync)
        {
            if (succeeded)
            {
                _successStreak++;
                _backoffMs = Math.Max(0, _backoffMs - 100);
                if (_successStreak >= _successesForScaleUp && _currentConcurrency < _maxConcurrency)
                {
                    _currentConcurrency++;
                    _successStreak = 0;
                    Trace.WriteLine($"ND-DIRECT throttle scale-up concurrency={_currentConcurrency} backoffMs={_backoffMs}");
                }

                return TimeSpan.Zero;
            }

            _successStreak = 0;

            var isThrottle = httpStatus == 429;
            var isTransient = httpStatus is 408 or 500 or 502 or 503 or 504;
            if (!isThrottle && !isTransient)
            {
                return TimeSpan.Zero;
            }

            if (_currentConcurrency > _minConcurrency)
            {
                _currentConcurrency--;
            }

            if (isThrottle)
            {
                _backoffMs = _backoffMs <= 0 ? 750 : Math.Min(15_000, _backoffMs * 2);
            }
            else
            {
                _backoffMs = _backoffMs <= 0 ? 400 : Math.Min(10_000, _backoffMs + 500);
            }

            var delay = retryAfter ?? TimeSpan.FromMilliseconds(_backoffMs);
            _blockedUntilUtc = DateTime.UtcNow + delay;
            Trace.WriteLine($"ND-DIRECT throttle scale-down status={httpStatus} concurrency={_currentConcurrency} backoffMs={_backoffMs} delayMs={delay.TotalMilliseconds:F0}");
            return delay;
        }
    }
}
