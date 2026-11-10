namespace StashLight.Core;

/// <summary>
/// Keeps requests at most <c>perSecond</c> per second across any number of workers: every caller reserves the next free time
/// slot and is told how long to wait for it. The clock is injectable so the behavior is unit tested without sleeping.
/// </summary>
public sealed class MinIntervalGate
{
    private readonly double _intervalMs;
    private readonly Func<double> _nowMs;
    private readonly object _lock = new();
    private double _nextFreeMs = double.MinValue;

    public MinIntervalGate(double perSecond, Func<double>? nowMs = null)
    {
        _intervalMs = perSecond <= 0 ? 0 : 1000.0 / perSecond;
        _nowMs = nowMs ?? (() => System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
    }

    /// <summary>Reserves a slot and returns how many milliseconds the caller must wait before sending (0 = go now).</summary>
    public double Reserve()
    {
        lock (_lock)
        {
            double now = _nowMs();
            double slot = Math.Max(now, _nextFreeMs);
            _nextFreeMs = slot + _intervalMs;
            return slot - now;
        }
    }

    public async Task WaitAsync(CancellationToken ct)
    {
        double wait = Reserve();
        if (wait > 0.5) await Task.Delay(TimeSpan.FromMilliseconds(wait), ct).ConfigureAwait(false);
    }
}
