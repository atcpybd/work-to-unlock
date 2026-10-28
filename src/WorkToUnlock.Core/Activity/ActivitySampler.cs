using WorkToUnlock.Core.Time;

namespace WorkToUnlock.Core.Activity;

/// <summary>Combines the platform monitors into one <see cref="ActivitySample"/>.</summary>
public sealed class ActivitySampler(IClock clock, IForegroundAppMonitor foreground, IIdleDetector idle, ISessionStateProvider session)
{
    public ActivitySample Capture()
    {
        var state = session.Current;

        // While locked, disconnected or suspended nothing qualifies, so skip the foreground lookup entirely.
        var app = state == SessionState.Active ? foreground.GetForegroundApp() : null;
        var idleDuration = state == SessionState.Active ? idle.GetIdleDuration() : TimeSpan.Zero;

        return new ActivitySample(clock.MonotonicMilliseconds, clock.UtcNow, app, idleDuration, state);
    }
}
