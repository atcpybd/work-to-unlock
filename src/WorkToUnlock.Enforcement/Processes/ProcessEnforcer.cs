using Microsoft.Extensions.Logging;
using WorkToUnlock.Core.Domain;
using WorkToUnlock.Core.Enforcement;
using WorkToUnlock.Core.Rules;
using WorkToUnlock.Core.Time;

namespace WorkToUnlock.Enforcement.Processes;

/// <summary>
/// Stage A, best-effort enforcement: watches running processes and closes restricted ones.
/// Apps that were already running when restrictions began are asked to close and, in
/// <see cref="EnforcementMode.CloseThenTerminate"/>, terminated after the grace period. Apps launched while
/// restrictions are active are terminated as soon as they are detected (they cannot have unsaved work yet).
/// This cannot prevent a launch: a process may run for up to one scan interval before it is detected.
/// </summary>
public sealed class ProcessEnforcer(
    IProcessInspector inspector,
    IApplicationRuleMatcher matcher,
    SystemFolders folders,
    IEnforcementNotifier notifier,
    IClock clock,
    ILogger<ProcessEnforcer> logger) : IAppEnforcer, IAsyncDisposable
{
    private const int FailuresBeforeFailedState = 3;

    private readonly Lock _lock = new();
    private readonly Dictionary<(int Pid, long Created), Tracked> _tracked = [];
    private IReadOnlyList<ApplicationRule> _rules = [];
    private EnforcementOptions _options = new();
    private bool _active;
    private bool _firstScan;
    private int _consecutiveFailures;
    private int _handledCount;
    private CancellationTokenSource? _loopCts;
    private Task? _loop;
    private EnforcementHealth _health = EnforcementHealth.Inactive(EnforcementMode.CloseThenTerminate, "Restrictions are not active.");

    public EnforcementHealth Health
    {
        get
        {
            lock (_lock)
            {
                return _health;
            }
        }
    }

    public event EventHandler<EnforcementHealth>? HealthChanged;

    public async Task ApplyRestrictionsAsync(IReadOnlyCollection<ApplicationRule> rules, EnforcementOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(options);

        var restricted = rules.Where(r => r.RuleType == RuleType.Restricted && r.Enabled).ToList();
        Task? previousLoop = null;
        lock (_lock)
        {
            _rules = restricted;
            if (!_active || _options.ScanInterval != options.ScanInterval)
            {
                if (!_active)
                {
                    _firstScan = true;
                    _tracked.Clear();
                    _consecutiveFailures = 0;
                }

                previousLoop = StopLoopLocked();
                _active = true;
                _loopCts = new CancellationTokenSource();
                var token = _loopCts.Token;
                _options = options;
                _loop = Task.Run(() => RunLoopAsync(options.ScanInterval, token), CancellationToken.None);
            }
            else
            {
                _options = options;
            }
        }

        if (previousLoop is not null)
        {
            await AwaitQuietly(previousLoop).ConfigureAwait(false);
        }

        logger.LogInformation("Restrictions applied to {Count} app(s) in {Mode} mode", restricted.Count, options.Mode);

        // Enforce immediately rather than waiting for the first timer tick.
        ScanOnce();
    }

    public async Task RemoveRestrictionsAsync(string reason, CancellationToken cancellationToken)
    {
        Task? loop;
        lock (_lock)
        {
            if (!_active)
            {
                SetHealthLocked(EnforcementHealth.Inactive(_options.Mode, reason));
                return;
            }

            _active = false;
            loop = StopLoopLocked();
            _tracked.Clear();
            SetHealthLocked(EnforcementHealth.Inactive(_options.Mode, reason));
        }

        if (loop is not null)
        {
            await AwaitQuietly(loop).ConfigureAwait(false);
        }

        logger.LogInformation("Restrictions removed: {Reason}", reason);
    }

    public async ValueTask DisposeAsync()
    {
        Task? loop;
        lock (_lock)
        {
            _active = false;
            loop = StopLoopLocked();
        }

        if (loop is not null)
        {
            await AwaitQuietly(loop).ConfigureAwait(false);
        }
    }

    /// <summary>One enforcement pass. Internal for tests.</summary>
    internal void ScanOnce()
    {
        lock (_lock)
        {
            if (!_active)
            {
                return;
            }

            try
            {
                ScanLocked();
                _consecutiveFailures = 0;
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                logger.LogError(ex, "Enforcement scan failed ({Failures} in a row)", _consecutiveFailures);
                if (_consecutiveFailures >= FailuresBeforeFailedState)
                {
                    SetHealthLocked(new EnforcementHealth
                    {
                        State = EnforcementHealthState.Failed,
                        Mode = _options.Mode,
                        RestrictionsApplied = true,
                        LastScanUtc = _health.LastScanUtc,
                        RestrictedProcessesHandled = _handledCount,
                        Message = "Enforcement is failing: running processes cannot be inspected. Restricted apps may run.",
                        Problems = [ex.Message],
                    });
                }
            }
        }
    }

    private void ScanLocked()
    {
        var now = clock.MonotonicMilliseconds;
        var options = _options;
        var problems = new List<string>();
        var seen = new HashSet<(int, long)>();

        foreach (var process in inspector.Snapshot())
        {
            if (process.ProcessId == inspector.CurrentProcessId || process.SessionId != inspector.CurrentSessionId || process.IsCritical)
            {
                continue;
            }

            var rule = matcher.Match(process.ExecutablePath, _rules, RuleType.Restricted);
            if (rule is null || !RuleSafetyValidator.IsSafeToEnforce(process.ExecutablePath, folders))
            {
                continue;
            }

            var key = (process.ProcessId, process.CreationTime);
            seen.Add(key);
            if (!_tracked.TryGetValue(key, out var tracked))
            {
                tracked = new Tracked(rule, now, _firstScan);
                _tracked[key] = tracked;
                if (!tracked.Preexisting)
                {
                    logger.LogInformation("Restricted app {Name} started (pid {Pid})", rule.Name, process.ProcessId);
                    notifier.OnLaunchBlocked(rule);
                }
            }

            if (!process.OwnedByCurrentUser)
            {
                // Never touch another user's processes. An inaccessible process of this session is usually elevated.
                if (!process.Accessible)
                {
                    problems.Add($"{rule.Name} is running with administrator rights and cannot be closed automatically.");
                }

                continue;
            }

            Enforce(process, tracked, now, options, problems);
        }

        foreach (var key in _tracked.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _tracked.Remove(key);
        }

        _firstScan = false;

        var degraded = problems.Count > 0;
        SetHealthLocked(new EnforcementHealth
        {
            State = degraded ? EnforcementHealthState.Degraded : EnforcementHealthState.Healthy,
            Mode = options.Mode,
            RestrictionsApplied = true,
            LastScanUtc = clock.UtcNow,
            RestrictedProcessesHandled = _handledCount,
            Message = degraded
                ? "Some restricted apps could not be closed."
                : options.Mode == EnforcementMode.CloseOnly
                    ? "Restricted apps are asked to close when detected. They are not force-closed."
                    : "Restricted apps are closed when detected. A newly started app may run briefly before it is closed.",
            Problems = problems.Distinct().ToList(),
        });
    }

    private void Enforce(ProcessEntry process, Tracked tracked, long now, EnforcementOptions options, List<string> problems)
    {
        var graceMs = (long)options.GracefulCloseTimeout.TotalMilliseconds;

        if (options.Mode == EnforcementMode.CloseOnly)
        {
            if (tracked.CloseRequestedAt is null || now - tracked.CloseRequestedAt.Value >= (long)options.RecloseInterval.TotalMilliseconds)
            {
                RequestClose(process, tracked, now, graceMs, notify: tracked.Preexisting && tracked.CloseRequestedAt is null);
            }

            return;
        }

        if (tracked.Preexisting)
        {
            if (tracked.CloseRequestedAt is null)
            {
                RequestClose(process, tracked, now, graceMs, notify: true);
                return;
            }

            if (now - tracked.CloseRequestedAt.Value < graceMs)
            {
                return;
            }
        }

        Terminate(process, tracked, problems);
    }

    private void RequestClose(ProcessEntry process, Tracked tracked, long now, long graceMs, bool notify)
    {
        var windows = inspector.RequestClose(process);
        tracked.CloseRequestedAt = now;
        logger.LogInformation("Asked {Name} (pid {Pid}) to close via {Count} window(s)", tracked.Rule.Name, process.ProcessId, windows);
        if (notify)
        {
            notifier.OnClosingRestrictedApp(tracked.Rule, TimeSpan.FromMilliseconds(graceMs));
        }
    }

    private void Terminate(ProcessEntry process, Tracked tracked, List<string> problems)
    {
        if (tracked.TerminationAttempts >= 3)
        {
            problems.Add($"{tracked.Rule.Name} could not be closed.");
            return;
        }

        tracked.TerminationAttempts++;
        var outcome = inspector.Terminate(process);
        switch (outcome)
        {
            case TerminationOutcome.Terminated:
                _handledCount++;
                logger.LogInformation("Terminated restricted app {Name} (pid {Pid})", tracked.Rule.Name, process.ProcessId);
                break;
            case TerminationOutcome.AccessDenied:
                problems.Add($"{tracked.Rule.Name} could not be closed: access denied (it may be running as administrator).");
                logger.LogWarning("Access denied terminating {Name} (pid {Pid})", tracked.Rule.Name, process.ProcessId);
                break;
            case TerminationOutcome.Failed:
                problems.Add($"{tracked.Rule.Name} could not be closed.");
                logger.LogWarning("Failed to terminate {Name} (pid {Pid})", tracked.Rule.Name, process.ProcessId);
                break;
        }
    }

    private async Task RunLoopAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                ScanOnce();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private Task? StopLoopLocked()
    {
        var loop = _loop;
        _loopCts?.Cancel();
        _loopCts?.Dispose();
        _loopCts = null;
        _loop = null;
        return loop;
    }

    private void SetHealthLocked(EnforcementHealth health)
    {
        var previous = _health;
        _health = health;
        var changed = previous.State != health.State
            || previous.Mode != health.Mode
            || previous.RestrictionsApplied != health.RestrictionsApplied
            || previous.Message != health.Message
            || !previous.Problems.SequenceEqual(health.Problems);
        if (changed)
        {
            HealthChanged?.Invoke(this, health);
        }
    }

    private static async Task AwaitQuietly(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed class Tracked(ApplicationRule rule, long firstSeen, bool preexisting)
    {
        public ApplicationRule Rule { get; } = rule;

        public long FirstSeen { get; } = firstSeen;

        /// <summary>Running when restrictions began (graceful close first) rather than launched during them.</summary>
        public bool Preexisting { get; } = preexisting;

        public long? CloseRequestedAt { get; set; }

        public int TerminationAttempts { get; set; }
    }
}
