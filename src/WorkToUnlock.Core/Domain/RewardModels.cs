namespace WorkToUnlock.Core.Domain;

/// <summary>Reward state machine states.</summary>
public enum RewardState
{
    /// <summary>Restricted apps are blocked; qualifying work accumulates.</summary>
    Working = 0,

    /// <summary>The work target is reached; restricted apps stay blocked until the user activates the reward.</summary>
    RewardReady = 1,

    /// <summary>The reward is active; restricted apps are available until the reward expires.</summary>
    Unlocked = 2,

    /// <summary>Transient: the reward elapsed. Never persisted; the engine moves straight on to <see cref="Working"/>.</summary>
    Expired = 3,
}

/// <summary>The single persisted reward-state row (RewardState table).</summary>
public sealed record RewardStateRecord
{
    public RewardState State { get; init; } = RewardState.Working;

    public long AccumulatedWorkMilliseconds { get; init; }

    public DateTimeOffset? RewardEarnedUtc { get; init; }

    public DateTimeOffset? RewardActivatedUtc { get; init; }

    public DateTimeOffset? RewardExpiresUtc { get; init; }

    /// <summary>Reward length captured at activation, so later setting changes do not alter an active reward.</summary>
    public long? RewardDurationMilliseconds { get; init; }

    /// <summary>Monotonic deadline captured at activation; only valid while <see cref="BootId"/> matches.</summary>
    public long? DeadlineMonotonicMilliseconds { get; init; }

    public string? BootId { get; init; }

    /// <summary>Optimistic-concurrency version. Incremented on every save.</summary>
    public long Version { get; init; }

    public static RewardStateRecord Initial { get; } = new();
}

public enum RewardTransitionKind
{
    Earned,
    Activated,
    Expired,
    Reset,
}

/// <summary>A state transition, recorded atomically with the new state (audit + reward history).</summary>
public sealed record RewardTransition(RewardTransitionKind Kind, DateTimeOffset TimestampUtc, string Description);

/// <summary>Read model of the reward engine for the UI, tray and enforcement.</summary>
public sealed record RewardStatus
{
    public RewardState State { get; init; }

    public TimeSpan AccumulatedWork { get; init; }

    public TimeSpan RequiredWork { get; init; }

    public TimeSpan RewardDuration { get; init; }

    public DateTimeOffset? RewardEarnedUtc { get; init; }

    public DateTimeOffset? RewardActivatedUtc { get; init; }

    public DateTimeOffset? RewardExpiresUtc { get; init; }

    public TimeSpan RemainingReward { get; init; }

    public TimeSpan RemainingWork => RequiredWork > AccumulatedWork ? RequiredWork - AccumulatedWork : TimeSpan.Zero;

    public bool CanActivate => State == RewardState.RewardReady;

    /// <summary>Restricted apps are available only while a reward is active.</summary>
    public bool RestrictionsActive => State != RewardState.Unlocked;

    public double ProgressFraction => RequiredWork <= TimeSpan.Zero
        ? 1
        : Math.Clamp(AccumulatedWork.TotalMilliseconds / RequiredWork.TotalMilliseconds, 0, 1);
}

public enum ActivationOutcome
{
    Activated,
    NotReady,
    AlreadyActive,
}

public sealed record ActivationResult(ActivationOutcome Outcome, RewardStatus Status)
{
    public bool Succeeded => Outcome == ActivationOutcome.Activated;
}
