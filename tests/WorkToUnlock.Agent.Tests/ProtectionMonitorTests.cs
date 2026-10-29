using Microsoft.Extensions.Logging.Abstractions;
using WorkToUnlock.Agent.Services;
using WorkToUnlock.Core.Domain;
using WorkToUnlock.Core.Persistence;

namespace WorkToUnlock.Agent.Tests;

public sealed class ProtectionMonitorTests : IDisposable
{
    private readonly AgentHarness _h = new();

    public void Dispose() => _h.Dispose();

    /// <summary>A new agent process over the same stored data.</summary>
    private ProtectionMonitor Restart() =>
        new(_h.SettingsStore, _h.Downtime, _h.Settings, _h.Rules, _h.Audit, _h.Clock, NullLogger<ProtectionMonitor>.Instance);

    private async Task<ProtectionMonitor> StartAsync()
    {
        await _h.Settings.LoadAsync(CancellationToken.None);
        await _h.Rules.LoadAsync(CancellationToken.None);
        await _h.Protection.StartAsync(CancellationToken.None);
        return _h.Protection;
    }

    [Fact]
    public async Task First_start_reports_nothing()
    {
        await StartAsync();

        Assert.Empty(_h.Downtime.Periods);
        Assert.True(_h.SettingsStore.Values.ContainsKey(ProtectionMonitor.HeartbeatKey));
    }

    [Fact]
    public async Task Ending_from_task_manager_is_reported_from_the_last_heartbeat()
    {
        var monitor = await StartAsync();
        _h.Clock.Advance(TimeSpan.FromMinutes(10));
        await monitor.BeatAsync(CancellationToken.None);
        var lastBeat = _h.Clock.UtcNow;

        _h.Clock.Advance(TimeSpan.FromMinutes(45)); // killed: no exit recorded
        await Restart().StartAsync(CancellationToken.None);

        var period = Assert.Single(_h.Downtime.Periods);
        Assert.Equal(DowntimeKinds.AgentNotRunning, period.Kind);
        Assert.Equal(lastBeat, period.StartUtc);
        Assert.Equal(_h.Clock.UtcNow, period.EndUtc);
        Assert.Contains(_h.Audit.Events, e => e.EventType == "AgentNotRunning" && e.Description.Contains("unexpectedly", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Closing_the_agent_is_reported()
    {
        var monitor = await StartAsync();
        monitor.MarkExiting(ProtectionMonitor.ExitClosed);
        await monitor.StopAsync(CancellationToken.None);

        _h.Clock.Advance(TimeSpan.FromMinutes(30));
        await Restart().StartAsync(CancellationToken.None);

        var period = Assert.Single(_h.Downtime.Periods);
        Assert.Equal(TimeSpan.FromMinutes(30), period.EndUtc - period.StartUtc);
        Assert.Contains(_h.Audit.Events, e => e.EventType == "AgentNotRunning" && e.Description.Contains("closed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Signing_out_or_shutting_down_is_not_reported()
    {
        var monitor = await StartAsync();
        monitor.MarkExiting(ProtectionMonitor.ExitSessionEnding);
        await monitor.StopAsync(CancellationToken.None);

        _h.Clock.Advance(TimeSpan.FromHours(3));
        await Restart().StartAsync(CancellationToken.None);

        Assert.Empty(_h.Downtime.Periods);
    }

    [Fact]
    public async Task A_restart_of_windows_is_not_reported()
    {
        await StartAsync();

        _h.Clock.Reboot(TimeSpan.FromHours(8)); // power loss: no exit recorded
        await Restart().StartAsync(CancellationToken.None);

        Assert.Empty(_h.Downtime.Periods);
    }

    [Fact]
    public async Task Short_gaps_like_crash_restarts_and_upgrades_are_not_reported()
    {
        await StartAsync();

        _h.Clock.Advance(TimeSpan.FromSeconds(90));
        await Restart().StartAsync(CancellationToken.None);

        Assert.Empty(_h.Downtime.Periods);
    }

    [Fact]
    public async Task Turning_enforcement_off_and_on_records_the_period()
    {
        var monitor = await StartAsync();

        await _h.Settings.SetEnforcementEnabledAsync(false, "testing", CancellationToken.None);
        await monitor.LastSync;
        var off = _h.Clock.UtcNow;
        _h.Clock.Advance(TimeSpan.FromMinutes(20));
        await _h.Settings.SetEnforcementEnabledAsync(true, string.Empty, CancellationToken.None);
        await monitor.LastSync;

        var period = Assert.Single(_h.Downtime.Periods);
        Assert.Equal(DowntimeKinds.EnforcementDisabled, period.Kind);
        Assert.Equal(off, period.StartUtc);
        Assert.Equal(off.AddMinutes(20), period.EndUtc);

        var report = await monitor.GetReportAsync(CancellationToken.None);
        Assert.Equal(1, report.TimesEnforcementDisabled);
        Assert.Equal(TimeSpan.FromMinutes(20), report.TimeEnforcementDisabled);
    }

    [Fact]
    public async Task Enforcement_left_off_across_restarts_stays_one_open_period()
    {
        var monitor = await StartAsync();
        await _h.Settings.SetEnforcementEnabledAsync(false, "testing", CancellationToken.None);
        await monitor.LastSync;

        _h.Clock.Advance(TimeSpan.FromMinutes(1));
        await Restart().StartAsync(CancellationToken.None);

        var period = Assert.Single(_h.Downtime.Periods);
        Assert.Null(period.EndUtc);
        Assert.True((await monitor.GetReportAsync(CancellationToken.None)).EnforcementDisabledNow);
    }

    private ApplicationRule Steam => _h.Rules.Rules.Single(r => r.Name == "Steam");

    [Fact]
    public async Task Turning_a_restricted_app_off_and_on_records_the_period_and_the_log()
    {
        var monitor = await StartAsync();

        await _h.Rules.SaveAsync(Steam with { Enabled = false }, CancellationToken.None);
        await monitor.LastSync;
        _h.Clock.Advance(TimeSpan.FromMinutes(40));
        await _h.Rules.SaveAsync(Steam with { Enabled = true }, CancellationToken.None);
        await monitor.LastSync;

        var period = Assert.Single(_h.Downtime.Periods);
        Assert.Equal(DowntimeKinds.AppAllowed, period.Kind);
        Assert.Equal("Steam", period.Subject);
        Assert.Equal(TimeSpan.FromMinutes(40), period.EndUtc - period.StartUtc);
        Assert.Contains(_h.Audit.Events, e => e.EventType == "RestrictedAppAllowed" && e.Description.Contains("Steam", StringComparison.Ordinal));
        Assert.Contains(_h.Audit.Events, e => e.EventType == "RestrictedAppBlocked");

        var report = await monitor.GetReportAsync(CancellationToken.None);
        Assert.Equal(1, report.TimesAppsAllowed);
        Assert.False(report.IsClean);
    }

    [Fact]
    public async Task Removing_a_restricted_app_is_recorded_as_a_moment()
    {
        var monitor = await StartAsync();

        await _h.Rules.DeleteAsync(Steam.Id, CancellationToken.None);
        await monitor.LastSync;

        var removed = Assert.Single(_h.Downtime.Periods);
        Assert.Equal(DowntimeKinds.AppUnrestricted, removed.Kind);
        Assert.Equal(removed.StartUtc, removed.EndUtc);
        Assert.Contains(_h.Audit.Events, e => e.EventType == "RestrictedAppRemoved");
    }

    [Fact]
    public async Task Changing_a_restricted_app_into_a_work_app_counts_as_removing_it()
    {
        var monitor = await StartAsync();

        await _h.Rules.SaveAsync(Steam with { RuleType = RuleType.Work }, CancellationToken.None);
        await monitor.LastSync;

        Assert.Equal(DowntimeKinds.AppUnrestricted, Assert.Single(_h.Downtime.Periods).Kind);
    }

    [Fact]
    public async Task An_app_left_off_across_restarts_stays_one_open_period_and_is_closed_once_restricted_again()
    {
        var monitor = await StartAsync();
        await _h.Rules.SaveAsync(Steam with { Enabled = false }, CancellationToken.None);
        await monitor.LastSync;

        _h.Clock.Advance(TimeSpan.FromMinutes(1));
        var restarted = Restart();
        await restarted.StartAsync(CancellationToken.None);
        Assert.Null(Assert.Single(_h.Downtime.Periods).EndUtc);

        await _h.Rules.SaveAsync(Steam with { Enabled = true }, CancellationToken.None);
        await restarted.LastSync;
        Assert.NotNull(Assert.Single(_h.Downtime.Periods).EndUtc);
    }

    [Fact]
    public async Task Apps_already_off_when_upgrading_are_counted_from_that_start()
    {
        await _h.RuleStore.SaveAsync(TestRules.Restricted(2, "Steam", TestRules.SteamPath) with { Enabled = false }, CancellationToken.None);

        await StartAsync();

        var period = Assert.Single(_h.Downtime.Periods);
        Assert.Equal(DowntimeKinds.AppAllowed, period.Kind);
        Assert.Equal(_h.Clock.UtcNow, period.StartUtc);
        Assert.DoesNotContain(_h.Audit.Events, e => e.EventType == "RestrictedAppAllowed");
    }
}