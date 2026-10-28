using WorkToUnlock.Core.Persistence;

namespace WorkToUnlock.Core.Domain;

/// <summary>Today's qualifying work against the daily goal, plus the goal streak.</summary>
public sealed record DailyProgress
{
    public TimeSpan Today { get; init; }

    public TimeSpan Goal { get; init; }

    public bool GoalEnabled => Goal > TimeSpan.Zero;

    public bool GoalMet { get; init; }

    /// <summary>Consecutive days the goal was met. Today counts once met; until then it does not break the streak.</summary>
    public int CurrentStreak { get; init; }

    public int BestStreak { get; init; }

    public int BlockedToday { get; init; }

    /// <summary>Most work in a single day in the available history.</summary>
    public TimeSpan BestDay { get; init; }

    /// <summary>All qualifying work in the available history.</summary>
    public TimeSpan TotalWork { get; init; }

    /// <summary>Days from the first day with work up to and including today; 0 without any work.</summary>
    public int DaysTracked { get; init; }

    public double Fraction => GoalEnabled ? Math.Clamp(Today.TotalMilliseconds / Goal.TotalMilliseconds, 0, 1) : 0;
}

public static class DailyProgressCalculator
{
    /// <param name="days">Daily totals, oldest first, ending with today.</param>
    public static DailyProgress Compute(IReadOnlyList<DailyActivity> days, TimeSpan goal)
    {
        ArgumentNullException.ThrowIfNull(days);
        var today = days.Count > 0 ? days[^1] : null;
        var bestDay = days.Count > 0 ? days.Max(d => d.QualifyingWork) : TimeSpan.Zero;
        var totalWork = days.Aggregate(TimeSpan.Zero, (sum, d) => sum + d.QualifyingWork);
        var firstActive = days.ToList().FindIndex(d => d.QualifyingWork > TimeSpan.Zero);
        var daysTracked = firstActive < 0 ? 0 : days.Count - firstActive;
        if (goal <= TimeSpan.Zero)
        {
            return new DailyProgress
            {
                Today = today?.QualifyingWork ?? TimeSpan.Zero,
                BlockedToday = today?.BlockedAttempts ?? 0,
                BestDay = bestDay,
                TotalWork = totalWork,
                DaysTracked = daysTracked,
            };
        }

        bool Met(DailyActivity day) => day.QualifyingWork >= goal;

        var best = 0;
        var run = 0;
        foreach (var day in days)
        {
            run = Met(day) ? run + 1 : 0;
            best = Math.Max(best, run);
        }

        var goalMetToday = today is not null && Met(today);
        var current = 0;
        for (var i = days.Count - (goalMetToday ? 1 : 2); i >= 0 && Met(days[i]); i--)
        {
            current++;
        }

        return new DailyProgress
        {
            Today = today?.QualifyingWork ?? TimeSpan.Zero,
            Goal = goal,
            GoalMet = goalMetToday,
            CurrentStreak = current,
            BestStreak = Math.Max(best, current),
            BlockedToday = today?.BlockedAttempts ?? 0,
            BestDay = bestDay,
            TotalWork = totalWork,
            DaysTracked = daysTracked,
        };
    }
}
