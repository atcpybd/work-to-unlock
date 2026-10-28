using WorkToUnlock.Core.Domain;
using WorkToUnlock.Core.Persistence;

namespace WorkToUnlock.Core.Tests;

public sealed class AchievementTests
{
    private static IReadOnlyList<string> Earned(AchievementFacts facts, params string[] alreadyUnlocked) =>
        AchievementCatalog.NewlyEarned(facts, alreadyUnlocked.ToHashSet()).Select(a => a.Id).ToList();

    [Fact]
    public void Nothing_is_earned_without_activity()
    {
        Assert.Empty(Earned(new AchievementFacts()));
    }

    [Fact]
    public void Work_totals_streaks_and_rewards_unlock_their_achievements()
    {
        var facts = new AchievementFacts
        {
            TotalWork = TimeSpan.FromHours(12),
            BestDay = TimeSpan.FromHours(2.5),
            BestStreak = 3,
            RewardsEarned = 1,
        };

        var earned = Earned(facts);

        Assert.Equal(["first-hour", "first-reward", "focus-2h", "goal-1", "streak-3", "total-10h"], earned.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Already_unlocked_achievements_are_not_earned_again()
    {
        var facts = new AchievementFacts { TotalWork = TimeSpan.FromHours(2) };

        Assert.Empty(Earned(facts, "first-hour"));
    }

    [Fact]
    public void A_clean_week_needs_a_full_week_of_use()
    {
        Assert.DoesNotContain("clean-week", Earned(new AchievementFacts { DaysTracked = 3, CleanLastWeek = true }));
        Assert.DoesNotContain("clean-week", Earned(new AchievementFacts { DaysTracked = 9, CleanLastWeek = false }));
        Assert.Contains("clean-week", Earned(new AchievementFacts { DaysTracked = 9, CleanLastWeek = true }));
    }

    [Fact]
    public void Achievement_ids_are_unique()
    {
        Assert.Equal(AchievementCatalog.All.Count, AchievementCatalog.All.Select(a => a.Id).Distinct().Count());
    }

    [Fact]
    public void Progress_reports_total_work_and_days_tracked()
    {
        var today = new DateOnly(2026, 3, 10);
        List<DailyActivity> days =
        [
            new(today.AddDays(-4), TimeSpan.Zero),
            new(today.AddDays(-3), TimeSpan.FromMinutes(30)),
            new(today.AddDays(-2), TimeSpan.Zero),
            new(today.AddDays(-1), TimeSpan.FromMinutes(90)),
            new(today, TimeSpan.FromMinutes(10)),
        ];

        var progress = DailyProgressCalculator.Compute(days, TimeSpan.Zero);

        Assert.Equal(TimeSpan.FromMinutes(130), progress.TotalWork);
        Assert.Equal(4, progress.DaysTracked);
    }
}
