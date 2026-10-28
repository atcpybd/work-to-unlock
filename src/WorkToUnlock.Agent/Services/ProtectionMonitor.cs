using System.Text.Json;
using Microsoft.Extensions.Logging;
using WorkToUnlock.Core.Domain;
using WorkToUnlock.Core.Persistence;
using WorkToUnlock.Core.Time;

namespace WorkToUnlock.Agent.Services;

/// <summary>
/// Records when restrictions were not protecting the user, for the honesty report: the agent ended while Windows kept
/// running (closed, ended from Task Manager, or crashed), or enforcement was turned off. A heartbeat is stored every
/// <see cref="HeartbeatInterval"/>; at start the previous run's last heartbeat shows how and when it ended.
/// </summary>
public sealed class ProtectionMonitor(
    ISettingsStore values,
    IDowntimeStore downtime,
    SettingsService settings,
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

    /// <summary>The most recent enforcement-change write (tests await it).</summary>
    internal Task LastSync { get; private set; } = Task.CompletedTask;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var previous = Heartbeat.Parse(await values.GetValueAsync(HeartbeatKey, cancellationToken).ConfigureAwait(false));
        if (previous is not null && WasUnprotected(previous, now, out var since))
        {
            var gap = now - since;
            await downtime.AddAsync(DowntimeKinds.AgentNotRunning, since, now, cancellationToken).ConfigureAwait(false);
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
        settings.Changed += (_, current) => QueueEnforcementSync(current.EnforcementEnabled);
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

    private void QueueEnforcementSync(bool enabled)
    {
        lock (_syncLock)
        {
            var previous = LastSync;
            LastSync = Task.Run(async () =>
            {
                await previous.ConfigureAwait(false);
                try
                {
                    await SyncEnforcementAsync(enabled, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not record the enforcement change");
                }
            });
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
            await downtime.CloseOpenAsync(DowntimeKinds.EnforcementDisabled, now, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var open = await downtime.GetSinceAsync(now, cancellationToken).ConfigureAwait(false);
            if (!open.Any(p => p.Kind == DowntimeKinds.EnforcementDisabled && p.EndUtc is null))
            {
                await downtime.AddAsync(DowntimeKinds.EnforcementDisabled, now, null, cancellationToken).ConfigureAwait(false);
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
