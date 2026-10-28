namespace WorkToUnlock.Agent.Services;

/// <summary>
/// "Run at most once per interval" on a monotonic millisecond clock. The first check is always due, and after a long
/// pause it fires once rather than catching up. Tracks the next due time, so there is no subtraction that can overflow.
/// </summary>
public sealed class Cadence(TimeSpan interval)
{
    private readonly long _intervalMs = (long)interval.TotalMilliseconds;
    private long? _nextDueMs;

    public bool IsDue(long nowMs)
    {
        if (_nextDueMs is long next && nowMs < next)
        {
            return false;
        }

        _nextDueMs = nowMs + _intervalMs;
        return true;
    }
}
