using System.Text.Json;
using Microsoft.Extensions.Logging;
using WorkToUnlock.Core.Domain;
using WorkToUnlock.Core.Persistence;
using WorkToUnlock.Core.Rules;
using WorkToUnlock.Core.Time;

namespace WorkToUnlock.Agent.Services;

/// <summary>
/// Records when restrictions were not protecting the user, for the honesty report: the agent ended while Windows kept
/// running (closed, ended from Task Manager, or crashed), enforcement was turned off, or a restricted app was turned
/// off or stopped being restricted. A heartbeat is stored every <see cref="HeartbeatInterval"/>; at start the
/// previous run's last heartbeat shows how and when it ended.
/// </summary>
public sealed class ProtectionMonitor(
    ISettingsStore values,
    IDowntimeStore downtime,
    SettingsService settings,
    IRuleCatalog rules,
    IAuditLog audit,
    IClock clock,
    ILogger<ProtectionMonitor> logger)
{
    internal const string HeartbeatKey = "Internal.Heartbeat";
    internal const int ReportDays = 7;
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    /// <summary>Shorter gaps (crash restarts, upgrades) are not reported.</summary>
    internal static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(2);

    /// <summary>Windows signed out, shut down, or an installer closed the agent. Never reported.</summary>
    public const string ExitSessionEnding = "session";

    /// <summary>Another program asked the agent to close.</summary>
    public const string ExitClosed = "closed";

    private const string ExitStopped = "stopped";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _syncLock = new();
    private string? _exitReason;
    private bool? _enforcementEnabled;
    private Dictionary<long, (string Name, bool Enabled)>? _restricted;

    /// <summary>The most recent enforcement or app-rule write (tests await it).</summary>
    internal Task LastSync { get; private set; } = Task.CompletedTask;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var previous = Heartbeat.Parse(await values.GetValueAsync(HeartbeatKey, cancellationToken).ConfigureAwait(false));
        if (previous is not null && WasUnprotected(previous, now, out var since))
        {
            var gap = now - since;
            await downtime.AddAsync(new DowntimePeriod(DowntimeKinds.AgentNotRunning, since, now), cancellationToken).ConfigureAwait(false);
            await audit.WriteAsync(
                "AgentNotRunning",
                previous.Exit is null
                    ? $"The agent ended unexpectedly (Task Manager or a crash) and was not running for {Describe(gap)}."
                    : $"The agent was closed and was not running for {Describe(gap)}.",
                "Warning",
                cancellationToken).ConfigureAwait(false);
            logger.LogWarning("The agent was not running for {Gap} before this start", gap);
        }

        await WriteHeartbeatAsync(null, cancellationToken).ConfigureAwait(false);
        await SyncEnforcementAsync(settings.Current.EnforcementEnabled, cancellationToken).ConfigureAwait(false);
        await SyncRulesAsync(cancellationToken).ConfigureAwait(false);
        settings.Changed += (_, current) => Queue(() => SyncEnforcementAsync(current.EnforcementEnabled, CancellationToken.None));
        rules.Changed += (_, _) => Queue(() => SyncRulesAsync(CancellationToken.None));
    }

    /// <summary>Called on the heartbeat cadence.</summary>
    public Task BeatAsync(CancellationToken cancellationToken) => WriteHeartbeatAsync(_exitReason, cancellationToken);

    /// <summary>
    /// Records why the agent is about to close. Blocks briefly: when Windows ends the session the process can be
    /// terminated as soon as the window message returns.
    /// </summary>
    public void MarkExiting(string reason)
    {
        _exitReason = reason;
        try
        {
            if (!Task.Run(() => WriteHeartbeatAsync(reason, CancellationToken.None)).Wait(TimeSpan.FromSeconds(3)))
            {
                logger.LogWarning("Recording the exit reason timed out");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not record the exit reason");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => WriteHeartbeatAsync(_exitReason ?? ExitStopped, cancellationToken);

    public async Task<TamperReport> GetReportAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var periods = await downtime.GetSinceAsync(now.AddDays(-ReportDays), cancellationToken).ConfigureAwait(false);
        return TamperReportCalculator.Compute(periods, now, ReportDays);
    }

    /// <summary>
    /// The previous run left the user unprotected when it ended within this boot of Windows, without Windows ending the
    /// session, and long enough ago to matter. After a restart the shutdown time is unknown, so nothing is reported.
    /// </summary>
    private bool WasUnprotected(Heartbeat previous, DateTimeOffset now, out DateTimeOffset since)
    {
        since = DateTimeOffset.FromUnixTimeMilliseconds(previous.Utc);
        var sameBoot = previous.Boot is not null && clock.BootId is not null
            ? previous.Boot == clock.BootId
            : since >= now - TimeSpan.FromMilliseconds(clock.MonotonicMilliseconds);
        return sameBoot && previous.Exit != ExitSessionEnding && now - since > Tolerance;
    }

    /// <summary>Runs a write after the previous one, off the thread that raised the change.</summary>
    private void Queue(Func<Task> write)
    {
        lock (_syncLock)
        {
            var previous = LastSync;
            LastSync = Task.Run(async () =>
            {
                await previous.ConfigureAwait(false);
                try
                {
                    await write().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not record a protection change");
                }
            });
        }
    }

    /// <summary>
    /// Opens a period while a restricted app is turned off and closes it when it is turned back on. Removing a
    /// restricted app, or changing it to another type, is recorded as a moment. The first call reconciles with the
    /// periods stored before this start.
    /// </summary>
    private async Task SyncRulesAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var current = rules.Rules.Where(r => r.RuleType == RuleType.Restricted).ToDictionary(r => r.Id, r => (r.Name, r.Enabled));

        if (_restricted is null)
        {
            var open = (await downtime.GetSinceAsync(now, cancellationToken).ConfigureAwait(false))
                .Where(p => p.Kind == DowntimeKinds.AppAllowed && p.EndUtc is null && p.RuleId is not null)
                .ToList();
            foreach (var period in open.Where(p => !(current.TryGetValue(p.RuleId!.Value, out var rule) && !rule.Enabled)))
            {
                await downtime.CloseOpenAsync(DowntimeKinds.AppAllowed, period.RuleId, now, cancellationToken).ConfigureAwait(false);
            }

            foreach (var (id, rule) in current.Where(r => !r.Value.Enabled && open.All(p => p.RuleId != r.Key)))
            {
                await downtime.AddAsync(new DowntimePeriod(DowntimeKinds.AppAllowed, now, null, id, rule.Name), cancellationToken).ConfigureAwait(false);
            }

            _restricted = current;
            return;
        }

        foreach (var (id, before) in _restricted)
        {
            if (!current.TryGetValue(id, out var after))
            {
                await downtime.CloseOpenAsync(DowntimeKinds.AppAllowed, id, now, cancellationToken).ConfigureAwait(false);
                await downtime.AddAsync(new DowntimePeriod(DowntimeKinds.AppUnrestricted, now, now, id, before.Name), cancellationToken).ConfigureAwait(false);
                await audit.WriteAsync("RestrictedAppRemoved", $"'{before.Name}' is no longer a restricted app.", "Warning", cancellationToken).ConfigureAwait(false);
            }
            else if (before.Enabled && !after.Enabled)
            {
                await AllowAsync(id, after.Name).ConfigureAwait(false);
            }
            else if (!before.Enabled && after.Enabled)
            {
                await downtime.CloseOpenAsync(DowntimeKinds.AppAllowed, id, now, cancellationToken).ConfigureAwait(false);
                await audit.WriteAsync("RestrictedAppBlocked", $"'{after.Name}' is restricted again.", "Success", cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var (id, added) in current.Where(r => !_restricted.ContainsKey(r.Key) && !r.Value.Enabled))
        {
            await AllowAsync(id, added.Name).ConfigureAwait(false);
        }

        _restricted = current;

        async Task AllowAsync(long id, string name)
        {
            await downtime.AddAsync(new DowntimePeriod(DowntimeKinds.AppAllowed, now, null, id, name), cancellationToken).ConfigureAwait(false);
            await audit.WriteAsync("RestrictedAppAllowed", $"'{name}' was turned off and can run at any time.", "Warning", cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Opens a period while enforcement is off and closes it when it is turned back on.</summary>
    private async Task SyncEnforcementAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (_enforcementEnabled == enabled)
        {
            return;
        }

        var now = clock.UtcNow;
        if (enabled)
        {
            await downtime.CloseOpenAsync(DowntimeKinds.EnforcementDisabled, null, now, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var open = await downtime.GetSinceAsync(now, cancellationToken).ConfigureAwait(false);
            if (!open.Any(p => p.Kind == DowntimeKinds.EnforcementDisabled && p.EndUtc is null))
            {
                await downtime.AddAsync(new DowntimePeriod(DowntimeKinds.EnforcementDisabled, now, null), cancellationToken).ConfigureAwait(false);
            }
        }

        _enforcementEnabled = enabled;
    }

    private async Task WriteHeartbeatAsync(string? exit, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(new Heartbeat(clock.UtcNow.ToUnixTimeMilliseconds(), clock.BootId, exit));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await values.SetValueAsync(HeartbeatKey, json, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string Describe(TimeSpan gap) =>
        gap.TotalHours >= 1 ? $"{(int)gap.TotalHours} h {gap.Minutes} min" : $"{Math.Max(1, (int)gap.TotalMinutes)} min";

    internal sealed record Heartbeat(long Utc, string? Boot, string? Exit)
    {
        public static Heartbeat? Parse(string? json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<Heartbeat>(json);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
