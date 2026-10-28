namespace WorkToUnlock.Core.Time;

/// <summary>
/// Combined wall clock and monotonic clock.
/// Wall-clock (UTC) time is used for persistence and display; the monotonic counter is used to
/// measure elapsed time so that wall-clock changes cannot inflate work or reward time.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }

    /// <summary>
    /// Milliseconds since an arbitrary fixed point in the current boot. Must never go backwards and must
    /// keep advancing while the machine sleeps (GetTickCount64 semantics on Windows).
    /// </summary>
    long MonotonicMilliseconds { get; }

    /// <summary>
    /// Identifies the current boot so that a persisted monotonic deadline can be reused after an agent
    /// restart within the same boot. Null when the platform cannot provide one.
    /// </summary>
    string? BootId { get; }
}

/// <summary>Production clock: <see cref="DateTimeOffset.UtcNow"/> plus <see cref="Environment.TickCount64"/>.</summary>
public sealed class SystemClock(string? bootId) : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public long MonotonicMilliseconds => Environment.TickCount64;

    public string? BootId { get; } = bootId;
}
