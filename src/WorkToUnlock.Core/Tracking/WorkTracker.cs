using Microsoft.Extensions.Logging;
using WorkToUnlock.Core.Activity;
using WorkToUnlock.Core.Domain;
using WorkToUnlock.Core.Persistence;
using WorkToUnlock.Core.Rewards;
using WorkToUnlock.Core.Rules;

namespace WorkToUnlock.Core.Tracking;

public sealed record TrackingOptions
{
    public TimeSpan IdleThreshold { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Intervals between samples longer than this are never credited. Protects against suspend/resume,
    /// debugger pauses and starved polling, where nobody can know what happened in the gap.
    /// </summary>
    public TimeSpan MaxSampleGap { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How often accumulated progress and the open work session are written to disk.</summary>
    public TimeSpan PersistInterval { get; init; } = TimeSpan.FromSeconds(15);
}

public interface IWorkTracker
{
    TrackingSnapshot Current { get; }

    /// <summary>False after a persistence failure, until the next successful save.</summary>
    bool PersistenceHealthy { get; }

    Task ProcessActivityAsync(ActivitySample sample, CancellationToken cancellationToken);

    Task FlushAsync(CancellationToken cancellationToken);

    void UpdateOptions(TrackingOptions options);
}

/// <summary>
/// Converts activity samples into qualifying work time.
/// The interval between two consecutive samples is credited only when both samples qualify and the interval is
/// shorter than <see cref="TrackingOptions.MaxSampleGap"/>. Elapsed time comes from the monotonic clock, so
/// irregular polling never over- or double-counts, and each interval is credited at most once.
/// </summary>
public sealed class WorkTracker(
    IRewardEngine engine,
    IRuleCatalog rules,
    IApplicationRuleMatcher matcher,
    IWorkSessionStore sessions,
    ILogger<WorkTracker> logger) : IWorkTracker, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile TrackingOptions _options = new();
    private volatile TrackingSnapshot _current = TrackingSnapshot.Unknown;
    private volatile bool _persistenceHealthy = true;
    private LastSample? _last;
    private long _heldMilliseconds;
    private OpenSession? _open;
    private long? _lastPersistMono;

    public TrackingSnapshot Current => _current;

    public bool PersistenceHealthy => _persistenceHealthy;

    public void UpdateOptions(TrackingOptions options) => _options = options ?? throw new ArgumentNullException(nameof(options));

    public async Task ProcessActivityAsync(ActivitySample sample, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var options = _options;
            var (status, rule) = Evaluate(sample, options);
            var previous = _last;

            // Record the sample before any I/O so a failed save can never cause an interval to be credited twice.
            _last = new LastSample(sample.MonotonicMilliseconds, status == ActivityStatus.Working ? rule : null);

            var delta = previous is null ? 0 : sample.MonotonicMilliseconds - previous.MonotonicMilliseconds;
            if (previous is null || delta > (long)options.MaxSampleGap.TotalMilliseconds)
            {
                if (delta > 0)
                {
                    logger.LogDebug("Skipped crediting a {Gap} ms sampling gap", delta);
                }

                _heldMilliseconds = 0;
            }
            else if (delta > 0)
            {
                await SettleIntervalAsync(previous.QualifyingRule, status, sample, delta, cancellationToken).ConfigureAwait(false);
            }

            _current = new TrackingSnapshot
            {
                Status = status,
                CurrentRuleId = rule?.Id,
                CurrentApplicationName = rule?.Name,
                IdleDuration = sample.IdleDuration,
                HeldWork = TimeSpan.FromMilliseconds(_heldMilliseconds),
                LastSampleUtc = sample.UtcNow,
            };

            if (status == ActivityStatus.Working && rule is not null)
            {
                if (_open is null || _open.RuleId != rule.Id)
                {
                    await CloseSessionAsync(cancellationToken).ConfigureAwait(false);
                    _open = new OpenSession(rule.Id, sample.UtcNow);
                }
            }
            else if (_open is not null)
            {
                await CloseSessionAsync(cancellationToken).ConfigureAwait(false);
            }

            _lastPersistMono ??= sample.MonotonicMilliseconds;
            if (sample.MonotonicMilliseconds - _lastPersistMono.Value >= (long)options.PersistInterval.TotalMilliseconds)
            {
                _lastPersistMono = sample.MonotonicMilliseconds;
                await PersistAsync(cancellationToken).ConfigureAwait(false);
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
            await PersistAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private (ActivityStatus Status, ApplicationRule? Rule) Evaluate(ActivitySample sample, TrackingOptions options)
    {
        switch (sample.Session)
        {
            case SessionState.Suspended:
                return (ActivityStatus.Suspended, null);
            case SessionState.Locked:
                return (ActivityStatus.Locked, null);
            case SessionState.Disconnected:
                return (ActivityStatus.Disconnected, null);
        }

        if (sample.Foreground is not { } foreground)
        {
            return (ActivityStatus.NoForeground, null);
        }

        var rule = matcher.Match(foreground.ExecutablePath, rules.Rules, RuleType.Work);
        if (rule is null)
        {
            return (ActivityStatus.NotWorkApp, null);
        }

        if (foreground.IsMinimized)
        {
            return (ActivityStatus.NoForeground, rule);
        }

        if (sample.IdleDuration >= options.IdleThreshold)
        {
            return (ActivityStatus.Idle, rule);
        }

        return (ActivityStatus.Working, rule);
    }

    /// <summary>
    /// Credits one sampling interval that started in a work app. Time after the last keyboard or mouse input is held
    /// back until new input shows the user is still there; it is dropped when the idle threshold is reached, the
    /// session locks, or the user leaves without any input. So time spent away from the PC never counts, while short
    /// pauses for reading or thinking do once the user continues.
    /// </summary>
    private async Task SettleIntervalAsync(ApplicationRule? previousRule, ActivityStatus status, ActivitySample sample, long delta, CancellationToken cancellationToken)
    {
        if (previousRule is null)
        {
            _heldMilliseconds = 0;
            return;
        }

        var idleMs = (long)Math.Min(sample.IdleDuration.TotalMilliseconds, long.MaxValue / 2);
        var inputDuringInterval = sample.Session == SessionState.Active && idleMs < delta;
        long confirmed;
        if (status == ActivityStatus.Working)
        {
            if (!inputDuringInterval)
            {
                _heldMilliseconds += delta;
                return;
            }

            // Everything up to the latest input is confirmed; the time since then is held again.
            confirmed = _heldMilliseconds + (delta - idleMs);
            _heldMilliseconds = idleMs;
        }
        else
        {
            // Left the work app (or went idle, or the session locked) during this interval. Input at that moment
            // proves the user was still there, so the pause before it counts; otherwise it is dropped.
            confirmed = inputDuringInterval ? _heldMilliseconds : 0;
            _heldMilliseconds = 0;
        }

        if (confirmed > 0)
        {
            await CreditAsync(previousRule, confirmed, sample.UtcNow, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CreditAsync(ApplicationRule rule, long milliseconds, DateTimeOffset utcNow, CancellationToken cancellationToken)
    {
        if (_open is not null && _open.RuleId == rule.Id)
        {
            _open.QualifyingMilliseconds += milliseconds;
            _open.EndUtc = utcNow;
        }

        if (!rule.CountsTowardGoal)
        {
            return;
        }

        try
        {
            await engine.AddWorkAsync(TimeSpan.FromMilliseconds(milliseconds), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The engine keeps the credit pending and retries the transition on the next credit or flush.
            _persistenceHealthy = false;
            logger.LogWarning(ex, "Failed to record qualifying work");
        }
    }

    private async Task CloseSessionAsync(CancellationToken cancellationToken)
    {
        if (_open is null)
        {
            return;
        }

        var session = _open;
        _open = null;
        try
        {
            await SaveSessionAsync(session, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _persistenceHealthy = false;
            logger.LogWarning(ex, "Failed to save work session for rule {RuleId}", session.RuleId);
        }
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        try
        {
            await engine.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (_open is not null)
            {
                await SaveSessionAsync(_open, cancellationToken).ConfigureAwait(false);
            }

            _persistenceHealthy = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _persistenceHealthy = false;
            logger.LogWarning(ex, "Failed to persist work progress; will retry");
        }
    }

    private async Task SaveSessionAsync(OpenSession session, CancellationToken cancellationToken)
    {
        if (session.QualifyingMilliseconds <= 0 || session.SavedMilliseconds == session.QualifyingMilliseconds)
        {
            return;
        }

        var record = new WorkSessionRecord
        {
            Id = session.Id ?? 0,
            ApplicationRuleId = session.RuleId,
            StartUtc = session.StartUtc,
            EndUtc = session.EndUtc,
            QualifyingMilliseconds = session.QualifyingMilliseconds,
        };

        if (session.Id is null)
        {
            session.Id = await sessions.InsertAsync(record, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await sessions.UpdateAsync(record, cancellationToken).ConfigureAwait(false);
        }

        session.SavedMilliseconds = record.QualifyingMilliseconds;
    }

    private sealed record LastSample(long MonotonicMilliseconds, ApplicationRule? QualifyingRule);

    private sealed class OpenSession(long ruleId, DateTimeOffset startUtc)
    {
        public long RuleId { get; } = ruleId;

        public DateTimeOffset StartUtc { get; } = startUtc;

        public DateTimeOffset EndUtc { get; set; } = startUtc;

        public long QualifyingMilliseconds { get; set; }

        public long SavedMilliseconds { get; set; }

        public long? Id { get; set; }
    }
}
