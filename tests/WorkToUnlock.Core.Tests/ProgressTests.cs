using WorkToUnlock.Core.Domain;
using WorkToUnlock.Core.Persistence;

namespace WorkToUnlock.Core.Tests;

public sealed class StreakTests
{
    private static readonly TimeSpan Goal = TimeSpan.FromHours(2);
    private static readonly DateOnly Today = new(2026, 3, 10);

    /// <summary>Builds days ending today from minutes worked, oldest first.</summary>
    private static List<DailyActivity> Days(params int[] minutes) =>
        minutes.Select((m, i) => new DailyActivity(Today.AddDays(i - minutes.Length + 1), TimeSpan.FromMinutes(m))).ToList();

    [Fact]
    public void Streak_includes_today_once_the_goal_is_met()
    {
        var progress = DailyProgressCalculator.Compute(Days(0, 130, 120, 125), Goal);

        Assert.True(progress.GoalMet);
        Assert.Equal(3, progress.CurrentStreak);
        Assert.Equal(TimeSpan.FromMinutes(125), progress.Today);
    }

    [Fact]
    public void Today_in_progress_does_not_break_the_streak()
    {
        var progress = DailyProgressCalculator.Compute(Days(120, 120, 30), Goal);

        Assert.False(progress.GoalMet);
        Assert.Equal(2, progress.CurrentStreak);
    }

    [Fact]
    public void A_missed_day_ends_the_streak()
    {
        var progress = DailyProgressCalculator.Compute(Days(120, 120, 120, 10, 120, 0), Goal);

        Assert.Equal(1, progress.CurrentStreak);
        Assert.Equal(3, progress.BestStreak);
    }

    [Fact]
    public void Best_streak_is_at_least_the_current_streak()
    {
        var progress = DailyProgressCalculator.Compute(Days(120, 120, 120, 120), Goal);

        Assert.Equal(4, progress.CurrentStreak);
        Assert.Equal(4, progress.BestStreak);
    }

    [Fact]
    public void Goal_turned_off_means_no_streak()
    {
        var progress = DailyProgressCalculator.Compute(Days(500, 500), TimeSpan.Zero);

        Assert.False(progress.GoalEnabled);
        Assert.False(progress.GoalMet);
        Assert.Equal(0, progress.CurrentStreak);
    }

    [Fact]
    public void Blocked_attempts_today_are_reported()
    {
        var days = Days(10, 20);
        days[^1] = days[^1] with { BlockedAttempts = 4 };

        Assert.Equal(4, DailyProgressCalculator.Compute(days, Goal).BlockedToday);
    }

    [Fact]
    public void No_history_is_an_empty_day()
    {
        var progress = DailyProgressCalculator.Compute([], Goal);

        Assert.Equal(TimeSpan.Zero, progress.Today);
        Assert.Equal(0, progress.CurrentStreak);
    }
}
