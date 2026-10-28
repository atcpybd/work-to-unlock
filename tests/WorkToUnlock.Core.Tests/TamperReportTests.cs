using WorkToUnlock.Core.Domain;
using WorkToUnlock.Core.Persistence;

namespace WorkToUnlock.Core.Tests;

public sealed class TamperReportTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);

    private static DowntimePeriod Period(string kind, double startHoursAgo, double? endHoursAgo) =>
        new(kind, Now.AddHours(-startHoursAgo), endHoursAgo is { } end ? Now.AddHours(-end) : null);

    [Fact]
    public void A_week_without_incidents_is_clean()
    {
        var report = TamperReportCalculator.Compute([], Now, days: 7);

        Assert.True(report.IsClean);
        Assert.Empty(report.Incidents);
    }

    [Fact]
    public void Unexpected_stops_and_disabled_enforcement_are_totalled()
    {
        DowntimePeriod[] periods =
        [
            Period(DowntimeKinds.AgentNotRunning, 30, 29.5),
            Period(DowntimeKinds.AgentNotRunning, 5, 4),
            Period(DowntimeKinds.EnforcementDisabled, 48, 47),
        ];

        var report = TamperReportCalculator.Compute(periods, Now, days: 7);

        Assert.False(report.IsClean);
        Assert.Equal(2, report.UnexpectedStops);
        Assert.Equal(TimeSpan.FromHours(1.5), report.TimeNotRunning);
        Assert.Equal(1, report.TimesEnforcementDisabled);
        Assert.Equal(TimeSpan.FromHours(1), report.TimeEnforcementDisabled);
        Assert.Equal(3, report.Incidents.Count);
        Assert.Equal(DowntimeKinds.AgentNotRunning, report.Incidents[0].Kind); // newest first
    }

    [Fact]
    public void Periods_are_clipped_to_the_report_window()
    {
        var report = TamperReportCalculator.Compute([Period(DowntimeKinds.EnforcementDisabled, 24 * 9, 24 * 6)], Now, days: 7);

        Assert.Equal(TimeSpan.FromDays(1), report.TimeEnforcementDisabled);
    }

    [Fact]
    public void Enforcement_still_disabled_counts_until_now()
    {
        var report = TamperReportCalculator.Compute([Period(DowntimeKinds.EnforcementDisabled, 3, null)], Now, days: 7);

        Assert.True(report.EnforcementDisabledNow);
        Assert.Equal(TimeSpan.FromHours(3), report.TimeEnforcementDisabled);
    }

    [Fact]
    public void Periods_entirely_before_the_window_are_ignored()
    {
        var report = TamperReportCalculator.Compute([Period(DowntimeKinds.AgentNotRunning, 24 * 20, 24 * 19)], Now, days: 7);

        Assert.True(report.IsClean);
    }
}
