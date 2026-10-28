using Microsoft.Extensions.Logging;
using WorkToUnlock.Core.Domain;
using WorkToUnlock.Core.Persistence;
using WorkToUnlock.Core.Time;

namespace WorkToUnlock.Core.Rewards;

/// <summary>
/// Work requirement and reward duration used by the engine. In <see cref="RewardMode.Balance"/> their ratio is the
/// break time earned per unit of work, saved up to <see cref="BalanceCap"/>.
/// </summary>
public sealed record RewardSettings(TimeSpan WorkRequirement, TimeSpan RewardDuration, RewardMode Mode = RewardMode.Goal, TimeSpan BalanceCap = default)
{
    public static RewardSettings From(AppSettings settings) => new(settings.WorkRequirement, settings.RewardDuration, settings.RewardMode, settings.BalanceCap);

    internal long CapMilliseconds => (long)BalanceCap.TotalMilliseconds;

    /// <summary>Break time earned by this much work.</summary>
    internal long BreakFor(long workMilliseconds) =>
        (long)Math.Round(workMilliseconds * RewardDuration.TotalMilliseconds / WorkRequirement.TotalMilliseconds);

    /// <summary>Work that earns this much break time.</summary>
    internal long WorkFor(long breakMilliseconds) =>
        (long)Math.Round(breakMilliseconds * WorkRequirement.TotalMilliseconds / RewardDuration.TotalMilliseconds);
}

public interface IRewardEngine
{
    /// <summary>Raised after a state transition has been durably persisted.</summary>
    event EventHandler<RewardStatus>? StateChanged;

    /// <summary>Cheap, lock-free snapshot of the current status.</summary>
    RewardStatus GetStatus();

    Task InitializeAsync(RewardSettings settings, CancellationToken cancellationToken);

    /// <summary>Credits qualifying work. Ignored unless the engine is in <see cref="RewardState.Working"/>.</summary>
    Task AddWorkAsync(TimeSpan qualifyingTime, CancellationToken cancellationToken);

    Task<ActivationResult> ActivateRewardAsync(CancellationToken cancellationToken);

    /// <summary>Expires an elapsed reward. Returns true when the state changed.</summary>
    Task<bool> TickAsync(CancellationToken cancellationToken);

    Task ResetProgressAsync(string reason, CancellationToken cancellationToken);

    /// <summary>Stops the countdown of a running break, keeping the time left. Returns true when it paused.</summary>
    Task<bool> PauseAsync(string reason, CancellationToken cancellationToken);

    /// <summary>Restarts a paused break with exactly the time that was left. Returns true when it resumed.</summary>
    Task<bool> ResumeAsync(CancellationToken cancellationToken);

    /// <summary>Balance mode: ends the running break and saves the time left. Returns true when a break ended.</summary>
    Task<bool> EndBreakEarlyAsync(CancellationToken cancellationToken);

    Task UpdateSettingsAsync(RewardSettings settings, CancellationToken cancellationToken);

    /// <summary>Persists accumulated work that has not been saved yet.</summary>
    Task FlushAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Deterministic reward state machine: WORKING → REWARD_READY → UNLOCKED → (EXPIRED) → WORKING.
/// All mutations are serialized, and every transition is persisted before <see cref="StateChanged"/> is raised,
/// so enforcement never acts on a state that is not durable.
/// </summary>
public sealed class RewardEngine(IRewardStateStore store, IClock clock, ILogger<RewardEngine> logger) : IRewardEngine, IDisposable
{
    /// <summary>A wall clock earlier than the activation time by more than this is treated as tampering.</summary>
    internal static readonly TimeSpan ClockRollbackTolerance = TimeSpan.FromMinutes(2);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile EngineState _state = new(RewardStateRecord.Initial, 0, null, new RewardSettings(TimeSpan.FromHours(1), TimeSpan.FromMinutes(20)));
    private bool _initialized;

    public event EventHandler<RewardStatus>? StateChanged;

    public RewardStatus GetStatus() => BuildStatus(_state);

    public async Task InitializeAsync(RewardSettings settings, CancellationToken cancellationToken)
    {
        ValidateSettings(settings);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var record = await store.LoadAsync(cancellationToken).ConfigureAwait(false) ?? RewardStateRecord.Initial;
            record = Sanitize(record, settings);
            _state = new EngineState(record, 0, RestoreDeadline(record), settings);
            _initialized = true;

            logger.LogInformation("Reward engine restored state {State} with {Accumulated} accumulated", record.State, TimeSpan.FromMilliseconds(record.AccumulatedWorkMilliseconds));

            // Progress saved under the other mode (the mode changed while the agent was stopping) is converted.
            await ApplyModeAsync(cancellationToken).ConfigureAwait(false);
            record = _state.Record;

            if (record.State == RewardState.Unlocked && Remaining(_state) <= TimeSpan.Zero)
            {
                await ExpireAsync("Reward elapsed while the agent was not running", cancellationToken).ConfigureAwait(false);
            }
            else if (settings.Mode == RewardMode.Goal && record.State == RewardState.Working && record.AccumulatedWorkMilliseconds >= (long)settings.WorkRequirement.TotalMilliseconds)
            {
                await EarnAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AddWorkAsync(TimeSpan qualifyingTime, CancellationToken cancellationToken)
    {
        if (qualifyingTime <= TimeSpan.Zero)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            var state = _state;
            if (state.Record.State != RewardState.Working)
            {
                // Rewards do not stack, and work does not accumulate during a reward.
                return;
            }

            var pending = state.PendingMilliseconds + (long)qualifyingTime.TotalMilliseconds;
            _state = state with { PendingMilliseconds = pending };

            // Balance mode: work turns into saved break time when it is flushed; there is no target to reach.
            if (state.Settings.Mode == RewardMode.Goal
                && state.Record.AccumulatedWorkMilliseconds + pending >= (long)state.Settings.WorkRequirement.TotalMilliseconds)
            {
                await EarnAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ActivationResult> ActivateRewardAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            var state = _state;
            var status = BuildStatus(state);
            if (state.Record.State == RewardState.Unlocked)
            {
                return new ActivationResult(ActivationOutcome.AlreadyActive, status);
            }

            if (!status.CanActivate)
            {
                return new ActivationResult(ActivationOutcome.NotReady, status);
            }

            // Goal mode unlocks the configured duration; balance mode spends everything saved (end early to keep the rest).
            var balanceMode = state.Settings.Mode == RewardMode.Balance;
            var durationMs = balanceMode ? (long)status.Balance.TotalMilliseconds : (long)state.Settings.RewardDuration.TotalMilliseconds;
            var now = clock.UtcNow;
            var mono = clock.MonotonicMilliseconds;
            var record = state.Record with
            {
                State = RewardState.Unlocked,
                AccumulatedWorkMilliseconds = balanceMode ? 0 : state.Record.AccumulatedWorkMilliseconds,
                BalanceMilliseconds = balanceMode ? 0 : state.Record.BalanceMilliseconds,
                RewardEarnedUtc = state.Record.RewardEarnedUtc ?? now,
                RewardActivatedUtc = now,
                RewardExpiresUtc = now.AddMilliseconds(durationMs),
                RewardDurationMilliseconds = durationMs,
                DeadlineMonotonicMilliseconds = mono + durationMs,
                BootId = clock.BootId,
            };

            var saved = await store.SaveAsync(
                record,
                state.Record.Version,
                new RewardTransition(
                    RewardTransitionKind.Activated,
                    now,
                    balanceMode ? $"Break started with {FormatDuration(TimeSpan.FromMilliseconds(durationMs))} of saved time" : $"Reward activated for {FormatDuration(state.Settings.RewardDuration)}"),
                cancellationToken).ConfigureAwait(false);

            _state = state with { Record = saved, PendingMilliseconds = 0, DeadlineMonotonic = mono + durationMs };
            logger.LogInformation("Reward activated until {Expires:u}", saved.RewardExpiresUtc);
            return new ActivationResult(ActivationOutcome.Activated, Publish());
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> TickAsync(CancellationToken cancellationToken)
    {
        // Fast path without the lock: nothing to expire.
        if (_state.Record.State != RewardState.Unlocked || Remaining(_state) > TimeSpan.Zero)
        {
            return false;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state.Record.State != RewardState.Unlocked || Remaining(_state) > TimeSpan.Zero)
            {
                return false;
            }

            await ExpireAsync("Reward time elapsed", cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ResetProgressAsync(string reason, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            var state = _state;
            var record = Cleared(state.Record) with { BalanceMilliseconds = 0 };
            var saved = await store.SaveAsync(
                record,
                state.Record.Version,
                new RewardTransition(RewardTransitionKind.Reset, clock.UtcNow, $"Progress reset: {reason}"),
                cancellationToken).ConfigureAwait(false);

            _state = state with { Record = saved, PendingMilliseconds = 0, DeadlineMonotonic = null };
            logger.LogInformation("Progress reset ({Reason})", reason);
            Publish();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> PauseAsync(string reason, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            var state = _state;
            if (state.Record.State != RewardState.Unlocked || state.Record.PausedRemainingMilliseconds is not null)
            {
                return false;
            }

            var remaining = Remaining(state);
            if (remaining <= TimeSpan.Zero)
            {
                await ExpireAsync("Reward time elapsed", cancellationToken).ConfigureAwait(false);
                return false;
            }

            var now = clock.UtcNow;
            var saved = await store.SaveAsync(
                state.Record with { PausedRemainingMilliseconds = (long)remaining.TotalMilliseconds, PausedUtc = now },
                state.Record.Version,
                new RewardTransition(RewardTransitionKind.Paused, now, $"Break paused with {FormatDuration(remaining)} left ({reason})"),
                cancellationToken).ConfigureAwait(false);

            _state = state with { Record = saved, DeadlineMonotonic = null };
            logger.LogInformation("Break paused with {Remaining} left ({Reason})", remaining, reason);
            Publish();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> ResumeAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            var state = _state;
            if (state.Record.State != RewardState.Unlocked || state.Record.PausedRemainingMilliseconds is not long left)
            {
                return false;
            }

            var now = clock.UtcNow;
            var mono = clock.MonotonicMilliseconds;
            var record = state.Record with
            {
                RewardExpiresUtc = now.AddMilliseconds(left),
                DeadlineMonotonicMilliseconds = mono + left,
                BootId = clock.BootId,
                PausedRemainingMilliseconds = null,
                PausedUtc = null,
            };
            var saved = await store.SaveAsync(
                record,
                state.Record.Version,
                new RewardTransition(RewardTransitionKind.Resumed, now, $"Break resumed with {FormatDuration(TimeSpan.FromMilliseconds(left))} left"),
                cancellationToken).ConfigureAwait(false);

            _state = state with { Record = saved, DeadlineMonotonic = mono + left };
            logger.LogInformation("Break resumed until {Expires:u}", saved.RewardExpiresUtc);
            Publish();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> EndBreakEarlyAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            var state = _state;
            if (state.Settings.Mode != RewardMode.Balance || state.Record.State != RewardState.Unlocked)
            {
                return false;
            }

            var left = (long)Remaining(state).TotalMilliseconds;
            var balance = Math.Min(state.Settings.CapMilliseconds, state.Record.BalanceMilliseconds + left);
            var saved = await store.SaveAsync(
                Cleared(state.Record) with { BalanceMilliseconds = balance },
                state.Record.Version,
                new RewardTransition(RewardTransitionKind.EndedEarly, clock.UtcNow, $"Break ended early; {FormatDuration(TimeSpan.FromMilliseconds(left))} saved for later"),
                cancellationToken).ConfigureAwait(false);

            _state = state with { Record = saved, PendingMilliseconds = 0, DeadlineMonotonic = null };
            logger.LogInformation("Break ended early with {Left} saved", TimeSpan.FromMilliseconds(left));
            Publish();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateSettingsAsync(RewardSettings settings, CancellationToken cancellationToken)
    {
        ValidateSettings(settings);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            _state = _state with { Settings = settings };
            await ApplyModeAsync(cancellationToken).ConfigureAwait(false);
            var state = _state;

            // An active reward keeps the duration captured at activation. A lowered requirement may complete the goal.
            if (settings.Mode == RewardMode.Goal
                && state.Record.State == RewardState.Working
                && state.Record.AccumulatedWorkMilliseconds + state.PendingMilliseconds >= (long)settings.WorkRequirement.TotalMilliseconds)
            {
                await EarnAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                Publish();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = _state;
            if (!_initialized || state.PendingMilliseconds == 0 || state.Record.State != RewardState.Working)
            {
                return;
            }

            var record = state.Settings.Mode == RewardMode.Balance
                ? state.Record with { BalanceMilliseconds = Balance(state) }
                : state.Record with { AccumulatedWorkMilliseconds = state.Record.AccumulatedWorkMilliseconds + state.PendingMilliseconds };
            var saved = await store.SaveAsync(record, state.Record.Version, null, cancellationToken).ConfigureAwait(false);
            _state = state with { Record = saved, PendingMilliseconds = 0 };
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    // ----- transitions (caller holds the gate) -----

    private async Task EarnAsync(CancellationToken cancellationToken)
    {
        var state = _state;
        var now = clock.UtcNow;
        var record = state.Record with
        {
            State = RewardState.RewardReady,
            AccumulatedWorkMilliseconds = (long)state.Settings.WorkRequirement.TotalMilliseconds,
            RewardEarnedUtc = now,
        };

        var saved = await store.SaveAsync(
            record,
            state.Record.Version,
            new RewardTransition(RewardTransitionKind.Earned, now, $"Work target of {FormatDuration(state.Settings.WorkRequirement)} reached"),
            cancellationToken).ConfigureAwait(false);

        _state = state with { Record = saved, PendingMilliseconds = 0 };
        logger.LogInformation("Reward earned");
        Publish();
    }

    private async Task ExpireAsync(string description, CancellationToken cancellationToken)
    {
        var state = _state;
        var saved = await store.SaveAsync(
            Cleared(state.Record),
            state.Record.Version,
            new RewardTransition(RewardTransitionKind.Expired, clock.UtcNow, description),
            cancellationToken).ConfigureAwait(false);

        _state = state with { Record = saved, PendingMilliseconds = 0, DeadlineMonotonic = null };
        logger.LogInformation("Reward expired: {Description}", description);
        Publish();
    }

    /// <summary>
    /// Converts progress kept for the other reward mode: switching to balance mode saves earned and partial progress
    /// as break time; switching to goal mode counts saved break time as work towards the goal. A running break
    /// continues unchanged. Unsaved work needs no conversion: each mode credits it in its own way when it is saved.
    /// </summary>
    private async Task ApplyModeAsync(CancellationToken cancellationToken)
    {
        var state = _state;
        var record = state.Record;
        var settings = state.Settings;
        RewardStateRecord converted;
        string description;

        if (settings.Mode == RewardMode.Balance)
        {
            var work = record.AccumulatedWorkMilliseconds;
            if (record.State == RewardState.Unlocked || (record.State == RewardState.Working && work == 0))
            {
                return;
            }

            var earned = record.State == RewardState.RewardReady ? (long)settings.RewardDuration.TotalMilliseconds : settings.BreakFor(work);
            converted = record with
            {
                State = RewardState.Working,
                AccumulatedWorkMilliseconds = 0,
                RewardEarnedUtc = null,
                BalanceMilliseconds = Math.Min(settings.CapMilliseconds, record.BalanceMilliseconds + earned),
            };
            description = $"Switched to earning as you go; {FormatDuration(TimeSpan.FromMilliseconds(earned))} of break time saved";
        }
        else
        {
            if (record.State != RewardState.Working || record.BalanceMilliseconds == 0)
            {
                return;
            }

            var work = Math.Min(
                (long)settings.WorkRequirement.TotalMilliseconds,
                record.AccumulatedWorkMilliseconds + settings.WorkFor(record.BalanceMilliseconds));
            converted = record with { AccumulatedWorkMilliseconds = work, BalanceMilliseconds = 0 };
            description = $"Switched to a work goal; saved break time counts as {FormatDuration(TimeSpan.FromMilliseconds(work))} of work";
        }

        var saved = await store.SaveAsync(
            converted,
            record.Version,
            new RewardTransition(RewardTransitionKind.ModeChanged, clock.UtcNow, description),
            cancellationToken).ConfigureAwait(false);
        _state = state with { Record = saved };
        logger.LogInformation("Reward mode applied: {Description}", description);
    }

    private RewardStatus Publish()
    {
        var status = BuildStatus(_state);
        try
        {
            StateChanged?.Invoke(this, status);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "A reward state listener failed");
        }

        return status;
    }

    // ----- helpers -----

    private static RewardStateRecord Cleared(RewardStateRecord record) => record with
    {
        State = RewardState.Working,
        AccumulatedWorkMilliseconds = 0,
        RewardEarnedUtc = null,
        RewardActivatedUtc = null,
        RewardExpiresUtc = null,
        RewardDurationMilliseconds = null,
        DeadlineMonotonicMilliseconds = null,
        BootId = null,
        PausedRemainingMilliseconds = null,
        PausedUtc = null,
    };

    /// <summary>Repairs impossible persisted values conservatively (towards restrictions being on).</summary>
    private RewardStateRecord Sanitize(RewardStateRecord record, RewardSettings settings)
    {
        if (!Enum.IsDefined(record.State) || record.State == RewardState.Expired)
        {
            logger.LogWarning("Persisted reward state {State} is invalid; treating as expired", record.State);
            return Cleared(record);
        }

        if (record.AccumulatedWorkMilliseconds < 0)
        {
            record = record with { AccumulatedWorkMilliseconds = 0 };
        }

        if (record.State == RewardState.Unlocked
            && (record.RewardActivatedUtc is null || record.RewardExpiresUtc is null || record.RewardDurationMilliseconds is null or <= 0))
        {
            logger.LogWarning("Persisted reward is missing its timestamps; ending it");
            return Cleared(record) with { Version = record.Version };
        }

        if (record.State == RewardState.RewardReady)
        {
            record = record with { AccumulatedWorkMilliseconds = Math.Max(record.AccumulatedWorkMilliseconds, (long)settings.WorkRequirement.TotalMilliseconds) };
        }

        if (record.State != RewardState.Unlocked && (record.PausedRemainingMilliseconds is not null || record.PausedUtc is not null))
        {
            record = record with { PausedRemainingMilliseconds = null, PausedUtc = null };
        }
        else if (record.PausedRemainingMilliseconds is < 0)
        {
            record = record with { PausedRemainingMilliseconds = 0 };
        }

        return record;
    }

    /// <summary>
    /// Rebuilds the monotonic deadline after a restart. Within the same boot the persisted monotonic deadline is
    /// authoritative; after a reboot only UTC is available, and a clock set earlier than the activation time ends
    /// the reward immediately so moving the clock back cannot extend it.
    /// </summary>
    private long? RestoreDeadline(RewardStateRecord record)
    {
        if (record.State != RewardState.Unlocked || record.PausedRemainingMilliseconds is not null)
        {
            return null; // a paused break gets a new deadline when it resumes
        }

        var mono = clock.MonotonicMilliseconds;
        if (record.BootId is not null && record.BootId == clock.BootId && record.DeadlineMonotonicMilliseconds is long deadline)
        {
            return deadline;
        }

        var now = clock.UtcNow;
        if (now < record.RewardActivatedUtc!.Value - ClockRollbackTolerance)
        {
            logger.LogWarning("System clock is earlier than the reward activation time; ending the reward");
            return mono;
        }

        var remaining = record.RewardExpiresUtc!.Value - now;
        var cap = TimeSpan.FromMilliseconds(record.RewardDurationMilliseconds!.Value);
        remaining = remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining > cap ? cap : remaining;
        return mono + (long)remaining.TotalMilliseconds;
    }

    /// <summary>
    /// Remaining reward time: the smaller of the monotonic and wall-clock estimates, capped at the reward duration.
    /// A wall clock moved backwards cannot extend a reward; one moved forwards can only shorten it.
    /// </summary>
    private TimeSpan Remaining(EngineState state)
    {
        var record = state.Record;
        if (record.State != RewardState.Unlocked || record.RewardExpiresUtc is null)
        {
            return TimeSpan.Zero;
        }

        var remaining = record.PausedRemainingMilliseconds is long paused
            ? TimeSpan.FromMilliseconds(paused)
            : record.RewardExpiresUtc.Value - clock.UtcNow;
        if (record.PausedRemainingMilliseconds is null && state.DeadlineMonotonic is long deadline)
        {
            var monoRemaining = TimeSpan.FromMilliseconds(deadline - clock.MonotonicMilliseconds);
            if (monoRemaining < remaining)
            {
                remaining = monoRemaining;
            }
        }

        if (record.RewardDurationMilliseconds is long durationMs && remaining > TimeSpan.FromMilliseconds(durationMs))
        {
            remaining = TimeSpan.FromMilliseconds(durationMs);
        }

        return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
    }

    /// <summary>Balance mode: saved break time including work not flushed yet, up to the cap.</summary>
    private static long Balance(EngineState state)
    {
        var pending = state.Record.State == RewardState.Working ? state.Settings.BreakFor(state.PendingMilliseconds) : 0;
        return Math.Min(state.Settings.CapMilliseconds, state.Record.BalanceMilliseconds + pending);
    }

    private RewardStatus BuildStatus(EngineState state)
    {
        var record = state.Record;
        var accumulated = record.State == RewardState.Working
            ? record.AccumulatedWorkMilliseconds + state.PendingMilliseconds
            : record.AccumulatedWorkMilliseconds;

        return new RewardStatus
        {
            State = record.State,
            AccumulatedWork = TimeSpan.FromMilliseconds(Math.Min(accumulated, (long)state.Settings.WorkRequirement.TotalMilliseconds)),
            RequiredWork = state.Settings.WorkRequirement,
            RewardDuration = record.State == RewardState.Unlocked && record.RewardDurationMilliseconds is long d
                ? TimeSpan.FromMilliseconds(d)
                : state.Settings.RewardDuration,
            RewardEarnedUtc = record.RewardEarnedUtc,
            RewardActivatedUtc = record.RewardActivatedUtc,
            RewardExpiresUtc = record.RewardExpiresUtc,
            RemainingReward = Remaining(state),
            IsPaused = record.State == RewardState.Unlocked && record.PausedRemainingMilliseconds is not null,
            Mode = state.Settings.Mode,
            Balance = state.Settings.Mode == RewardMode.Balance ? TimeSpan.FromMilliseconds(Balance(state)) : TimeSpan.Zero,
            BalanceCap = state.Settings.BalanceCap,
        };
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("The reward engine has not been initialized.");
        }
    }

    private static void ValidateSettings(RewardSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.WorkRequirement <= TimeSpan.Zero || settings.RewardDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "Work requirement and reward duration must be positive.");
        }

        if (settings.Mode == RewardMode.Balance && settings.BalanceCap <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "Balance mode needs a positive balance limit.");
        }
    }

    internal static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1 && duration.Minutes == 0
        ? $"{(int)duration.TotalHours} h"
        : duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours} h {duration.Minutes} min"
            : $"{(int)Math.Round(duration.TotalMinutes)} min";

    private sealed record EngineState(RewardStateRecord Record, long PendingMilliseconds, long? DeadlineMonotonic, RewardSettings Settings);
}
