namespace WorkToUnlock.Core.Domain;

/// <summary>What achievements are judged on. Totals cover the stored history (limited by data retention).</summary>
public sealed record AchievementFacts
{
    public TimeSpan TotalWork { get; init; }

    public TimeSpan BestDay { get; init; }

    public int BestStreak { get; init; }

    public int RewardsEarned { get; init; }

    public int DaysTracked { get; init; }

    /// <summary>No unprotected time (agent closed, enforcement off) in the last 7 days.</summary>
    public bool CleanLastWeek { get; init; }
}

public sealed record AchievementDefinition(string Id, string Title, string Description, string Glyph, Func<AchievementFacts, bool> IsEarned);

/// <summary>An achievement as shown in the dashboard.</summary>
public sealed record AchievementInfo(string Id, string Title, string Description, string Glyph, DateTimeOffset? UnlockedUtc)
{
    public bool Unlocked => UnlockedUtc is not null;
}

public static class AchievementCatalog
{
    private const string Stopwatch = "", Star = "", Bolt = "", Flag = "", Calendar = "", Clock = "", Lock = "";

    /// <summary>In display order. Ids are stored in the database and must never change.</summary>
    public static IReadOnlyList<AchievementDefinition> All { get; } =
    [
        new("first-hour", "Warm-up", "Work for 1 hour in total", Stopwatch, f => f.TotalWork >= TimeSpan.FromHours(1)),
        new("first-reward", "First break", "Earn your first reward", Star, f => f.RewardsEarned >= 1),
        new("focus-2h", "In the zone", "Work 2 hours in one day", Bolt, f => f.BestDay >= TimeSpan.FromHours(2)),
        new("focus-4h", "Deep work", "Work 4 hours in one day", Bolt, f => f.BestDay >= TimeSpan.FromHours(4)),
        new("goal-1", "Goal getter", "Reach your daily goal", Flag, f => f.BestStreak >= 1),
        new("streak-3", "On a roll", "Reach your daily goal 3 days in a row", Calendar, f => f.BestStreak >= 3),
        new("streak-7", "Full week", "Reach your daily goal 7 days in a row", Calendar, f => f.BestStreak >= 7),
        new("streak-30", "Unstoppable", "Reach your daily goal 30 days in a row", Calendar, f => f.BestStreak >= 30),
        new("total-10h", "Ten hours", "Work for 10 hours in total", Clock, f => f.TotalWork >= TimeSpan.FromHours(10)),
        new("total-100h", "Centurion", "Work for 100 hours in total", Clock, f => f.TotalWork >= TimeSpan.FromHours(100)),
        new("rewards-25", "Well earned", "Earn 25 rewards", Star, f => f.RewardsEarned >= 25),
        new("clean-week", "Clean week", "7 days with no unprotected time", Lock, f => f.DaysTracked >= 7 && f.CleanLastWeek),
    ];

    public static IReadOnlyList<AchievementDefinition> NewlyEarned(AchievementFacts facts, IReadOnlySet<string> unlocked)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(unlocked);
        return All.Where(a => !unlocked.Contains(a.Id) && a.IsEarned(facts)).ToList();
    }

    public static IReadOnlyList<AchievementInfo> Describe(IReadOnlyDictionary<string, DateTimeOffset> unlocked)
    {
        ArgumentNullException.ThrowIfNull(unlocked);
        return All.Select(a => new AchievementInfo(a.Id, a.Title, a.Description, a.Glyph, unlocked.TryGetValue(a.Id, out var when) ? when : null)).ToList();
    }
}
