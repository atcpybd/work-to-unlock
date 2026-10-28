using WorkToUnlock.Core.Persistence;

namespace WorkToUnlock.Core.Domain;

public sealed record TamperIncident(string Kind, DateTimeOffset StartUtc, DateTimeOffset? EndUtc, TimeSpan Duration);

/// <summary>
/// When restrictions were not protecting the user during the report window: the agent ended without closing
/// normally, or enforcement was turned off. Makes time off visible instead of silent.
/// </summary>
public sealed record TamperReport
{
    public int Days { get; init; }

    public int UnexpectedStops { get; init; }

    public TimeSpan TimeNotRunning { get; init; }

    public int TimesEnforcementDisabled { get; init; }

    public TimeSpan TimeEnforcementDisabled { get; init; }

    public bool EnforcementDisabledNow { get; init; }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<TamperIncident> Incidents { get; init; } = [];

    public bool IsClean => UnexpectedStops == 0 && TimesEnforcementDisabled == 0;
}

public static class TamperReportCalculator
{
    public static TamperReport Compute(IReadOnlyCollection<DowntimePeriod> periods, DateTimeOffset now, int days)
    {
        ArgumentNullException.ThrowIfNull(periods);
        var windowStart = now.AddDays(-days);
        var incidents = periods
            .Where(p => (p.EndUtc ?? now) > windowStart && p.StartUtc <= now)
            .Select(p =>
            {
                var start = p.StartUtc < windowStart ? windowStart : p.StartUtc;
                var end = p.EndUtc ?? now;
                return new TamperIncident(p.Kind, p.StartUtc, p.EndUtc, end > start ? end - start : TimeSpan.Zero);
            })
            .OrderByDescending(i => i.StartUtc)
            .ToList();

        TimeSpan Total(string kind) => incidents.Where(i => i.Kind == kind).Aggregate(TimeSpan.Zero, (sum, i) => sum + i.Duration);

        return new TamperReport
        {
            Days = days,
            UnexpectedStops = incidents.Count(i => i.Kind == DowntimeKinds.AgentNotRunning),
            TimeNotRunning = Total(DowntimeKinds.AgentNotRunning),
            TimesEnforcementDisabled = incidents.Count(i => i.Kind == DowntimeKinds.EnforcementDisabled),
            TimeEnforcementDisabled = Total(DowntimeKinds.EnforcementDisabled),
            EnforcementDisabledNow = incidents.Any(i => i.Kind == DowntimeKinds.EnforcementDisabled && i.EndUtc is null),
            Incidents = incidents,
        };
    }
}
