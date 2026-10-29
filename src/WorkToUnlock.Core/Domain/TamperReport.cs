using WorkToUnlock.Core.Persistence;

namespace WorkToUnlock.Core.Domain;

/// <param name="Subject">The restricted app concerned, for app incidents.</param>
public sealed record TamperIncident(string Kind, DateTimeOffset StartUtc, DateTimeOffset? EndUtc, TimeSpan Duration, string? Subject = null);

/// <summary>
/// When restrictions were not protecting the user during the report window: the agent ended without closing
/// normally, enforcement was turned off, or a restricted app was allowed or stopped being restricted. Makes time off
/// visible instead of silent.
/// </summary>
public sealed record TamperReport
{
    public int Days { get; init; }

    public int UnexpectedStops { get; init; }

    public TimeSpan TimeNotRunning { get; init; }

    public int TimesEnforcementDisabled { get; init; }

    public TimeSpan TimeEnforcementDisabled { get; init; }

    public bool EnforcementDisabledNow { get; init; }

    /// <summary>Times a restricted app was turned off so it could run.</summary>
    public int TimesAppsAllowed { get; init; }

    /// <summary>Total time per allowed app, added up across apps.</summary>
    public TimeSpan TimeAppsAllowed { get; init; }

    /// <summary>Restricted apps that are turned off right now.</summary>
    public IReadOnlyList<string> AppsAllowedNow { get; init; } = [];

    /// <summary>Restricted apps removed or changed to another type.</summary>
    public int AppsUnrestricted { get; init; }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<TamperIncident> Incidents { get; init; } = [];

    public bool IsClean => UnexpectedStops == 0 && TimesEnforcementDisabled == 0 && TimesAppsAllowed == 0 && AppsUnrestricted == 0;
}

public static class TamperReportCalculator
{
    public static TamperReport Compute(IReadOnlyCollection<DowntimePeriod> periods, DateTimeOffset now, int days)
    {
        ArgumentNullException.ThrowIfNull(periods);
        var windowStart = now.AddDays(-days);
        var incidents = periods
            .Where(p => (p.EndUtc ?? now) >= windowStart && p.StartUtc <= now)
            .Select(p =>
            {
                var start = p.StartUtc < windowStart ? windowStart : p.StartUtc;
                var end = p.EndUtc ?? now;
                return new TamperIncident(p.Kind, p.StartUtc, p.EndUtc, end > start ? end - start : TimeSpan.Zero, p.Subject);
            })
            .OrderByDescending(i => i.StartUtc)
            .ToList();

        int Count(string kind) => incidents.Count(i => i.Kind == kind);
        TimeSpan Total(string kind) => incidents.Where(i => i.Kind == kind).Aggregate(TimeSpan.Zero, (sum, i) => sum + i.Duration);
        bool OpenNow(TamperIncident i) => i.EndUtc is null;

        return new TamperReport
        {
            Days = days,
            UnexpectedStops = Count(DowntimeKinds.AgentNotRunning),
            TimeNotRunning = Total(DowntimeKinds.AgentNotRunning),
            TimesEnforcementDisabled = Count(DowntimeKinds.EnforcementDisabled),
            TimeEnforcementDisabled = Total(DowntimeKinds.EnforcementDisabled),
            EnforcementDisabledNow = incidents.Any(i => i.Kind == DowntimeKinds.EnforcementDisabled && OpenNow(i)),
            TimesAppsAllowed = Count(DowntimeKinds.AppAllowed),
            TimeAppsAllowed = Total(DowntimeKinds.AppAllowed),
            AppsAllowedNow = incidents
                .Where(i => i.Kind == DowntimeKinds.AppAllowed && OpenNow(i) && i.Subject is not null)
                .Select(i => i.Subject!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            AppsUnrestricted = Count(DowntimeKinds.AppUnrestricted),
            Incidents = incidents,
        };
    }
}
