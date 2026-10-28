using Microsoft.Extensions.Logging.Abstractions;
using WorkToUnlock.Agent.Services;
using WorkToUnlock.Core.Domain;
using WorkToUnlock.Core.Persistence;

namespace WorkToUnlock.Agent.Tests;

public sealed class AchievementServiceTests : IDisposable
{
    private readonly AgentHarness _h = new();
    private readonly AchievementService _achievements;

    public AchievementServiceTests()
    {
        _achievements = new AchievementService(_h.Achievements, _h.Statistics, _h.Progress, _h.Protection, _h.Notifications, _h.Clock, NullLogger<AchievementService>.Instance);
    }

    public void Dispose() => _h.Dispose();

    private void WorkedToday(int minutes) =>
        _h.Statistics.Days = [new DailyActivity(DateOnly.FromDateTime(_h.Clock.UtcNow.UtcDateTime), TimeSpan.FromMinutes(minutes))];

    private async Task RecalculateAsync()
    {
        await _h.Progress.GetAsync(CancellationToken.None, refresh: true);
        await _achievements.LastEvaluation;
    }

    [Fact]
    public async Task Progress_updates_unlock_and_announce_new_achievements()
    {
        await _h.StartAsync();
        _achievements.Start();

        WorkedToday(70);
        await RecalculateAsync();

        Assert.Contains("first-hour", _h.Achievements.Unlocked.Keys);
        Assert.Single(_h.Tray.Notifications, n => n.Title == "Achievement unlocked: Warm-up");
    }

    [Fact]
    public async Task Several_at_once_are_announced_together_and_only_once()
    {
        await _h.StartAsync();
        _achievements.Start();
        _h.Statistics.Report = new StatisticsReport { RewardsEarned = 1 };

        WorkedToday(150);
        await RecalculateAsync();
        await RecalculateAsync();

        Assert.Equal(["first-hour", "first-reward", "focus-2h", "goal-1"], _h.Achievements.Unlocked.Keys.Order(StringComparer.Ordinal));
        Assert.Single(_h.Tray.Notifications, n => n.Title == "4 achievements unlocked");
        Assert.Single(_h.Tray.Notifications, n => n.Title.Contains("chievement", StringComparison.Ordinal));
    }

    [Fact]
    public async Task All_achievements_are_listed_with_their_unlock_time()
    {
        _h.Achievements.Unlocked["goal-1"] = _h.Clock.UtcNow;

        var all = await _achievements.GetAllAsync(CancellationToken.None);

        Assert.Equal(AchievementCatalog.All.Count, all.Count);
        Assert.True(all.Single(a => a.Id == "goal-1").Unlocked);
        Assert.False(all.Single(a => a.Id == "first-hour").Unlocked);
    }
}
