namespace WorkToUnlock.Core.Activity;

/// <summary>The process that owns the foreground window. Window titles are deliberately not captured.</summary>
public sealed record ForegroundAppInfo(int ProcessId, string ExecutablePath, bool IsMinimized);

/// <summary>Interactive session state as seen by the agent.</summary>
public enum SessionState
{
    Active = 0,
    Locked = 1,
    Disconnected = 2,
    Suspended = 3,
}

/// <summary>One observation taken by the agent's sampling loop.</summary>
public readonly record struct ActivitySample(
    long MonotonicMilliseconds,
    DateTimeOffset UtcNow,
    ForegroundAppInfo? Foreground,
    TimeSpan IdleDuration,
    SessionState Session);

/// <summary>Why the current moment does or does not count as work.</summary>
public enum ActivityStatus
{
    /// <summary>An approved app is in the foreground and the user is active.</summary>
    Working = 0,

    Idle = 1,

    /// <summary>The foreground app is not an approved work app.</summary>
    NotWorkApp = 2,

    Locked = 3,

    Disconnected = 4,

    Suspended = 5,

    /// <summary>No foreground window, or the approved app is minimized.</summary>
    NoForeground = 6,

    /// <summary>Monitoring has not produced a sample yet.</summary>
    Unknown = 7,
}

/// <summary>Live tracking state exposed to the dashboard.</summary>
public sealed record TrackingSnapshot
{
    public ActivityStatus Status { get; init; } = ActivityStatus.Unknown;

    public long? CurrentRuleId { get; init; }

    public string? CurrentApplicationName { get; init; }

    public TimeSpan IdleDuration { get; init; }

    public DateTimeOffset? LastSampleUtc { get; init; }

    public static TrackingSnapshot Unknown { get; } = new();
}

public interface IForegroundAppMonitor
{
    /// <summary>Returns the foreground application, or null when there is none or it cannot be identified.</summary>
    ForegroundAppInfo? GetForegroundApp();
}

public interface IIdleDetector
{
    /// <summary>Time since the last keyboard or mouse input in the interactive session.</summary>
    TimeSpan GetIdleDuration();
}

public interface ISessionStateProvider
{
    SessionState Current { get; }

    event EventHandler<SessionState>? Changed;
}
