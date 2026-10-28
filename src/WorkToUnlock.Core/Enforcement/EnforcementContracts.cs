using WorkToUnlock.Core.Domain;

namespace WorkToUnlock.Core.Enforcement;

public enum EnforcementHealthState
{
    /// <summary>Restrictions are not needed right now (reward active or enforcement disabled).</summary>
    Inactive = 0,

    /// <summary>Restrictions are being enforced normally.</summary>
    Healthy = 1,

    /// <summary>Enforcement is running but some restricted processes could not be handled.</summary>
    Degraded = 2,

    /// <summary>Enforcement is not working (for example the scan loop is failing).</summary>
    Failed = 3,
}

/// <summary>Honest report of what the enforcement engine can currently guarantee.</summary>
public sealed record EnforcementHealth
{
    public EnforcementHealthState State { get; init; } = EnforcementHealthState.Inactive;

    public EnforcementMode Mode { get; init; } = EnforcementMode.CloseThenTerminate;

    public bool RestrictionsApplied { get; init; }

    /// <summary>True when launch prevention (AppLocker) is in effect, not only process monitoring.</summary>
    public bool LaunchPreventionActive { get; init; }

    public DateTimeOffset? LastScanUtc { get; init; }

    public int RestrictedProcessesHandled { get; init; }

    public string Message { get; init; } = "Restrictions are not active.";

    public IReadOnlyList<string> Problems { get; init; } = [];

    public static EnforcementHealth Inactive(EnforcementMode mode, string message) => new()
    {
        State = EnforcementHealthState.Inactive,
        Mode = mode,
        Message = message,
    };
}

/// <summary>Enforcement behaviour derived from settings.</summary>
public sealed record EnforcementOptions
{
    public EnforcementMode Mode { get; init; } = EnforcementMode.CloseThenTerminate;

    /// <summary>How long a restricted app that was already running may take to close before it is terminated.</summary>
    public TimeSpan GracefulCloseTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan ScanInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>In close-only mode, how often a still-running restricted app is asked again to close.</summary>
    public TimeSpan RecloseInterval { get; init; } = TimeSpan.FromSeconds(30);

    public static EnforcementOptions From(AppSettings settings) => new()
    {
        Mode = settings.EnforcementMode,
        GracefulCloseTimeout = settings.GracefulCloseTimeout,
    };
}

public interface IAppEnforcer
{
    EnforcementHealth Health { get; }

    event EventHandler<EnforcementHealth>? HealthChanged;

    /// <summary>
    /// Starts (or updates) enforcement for the given restricted rules. Idempotent: calling it again with a new rule
    /// set updates the active restrictions without treating already-handled processes as new.
    /// </summary>
    Task ApplyRestrictionsAsync(IReadOnlyCollection<ApplicationRule> rules, EnforcementOptions options, CancellationToken cancellationToken);

    /// <summary>Stops enforcement; restricted applications may run.</summary>
    Task RemoveRestrictionsAsync(string reason, CancellationToken cancellationToken);
}

/// <summary>Enforcement events the agent turns into (rate-limited) user notifications.</summary>
public interface IEnforcementNotifier
{
    /// <summary>A restricted app was started while restrictions are active.</summary>
    void OnLaunchBlocked(ApplicationRule rule);

    /// <summary>A restricted app that was already running has been asked to close.</summary>
    void OnClosingRestrictedApp(ApplicationRule rule, TimeSpan gracePeriod);
}
