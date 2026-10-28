using System.Collections.Concurrent;
using WorkToUnlock.Core.Domain;
using WorkToUnlock.Core.Enforcement;
using WorkToUnlock.Core.Rewards;
using WorkToUnlock.Core.Time;

namespace WorkToUnlock.Agent.Services;

/// <summary>
/// Turns reward and enforcement events into tray notifications. Rate-limited per app and respects the
/// "Notifications" setting.
/// </summary>
public sealed class NotificationService(ITrayPresenter tray, SettingsService settings, IRewardEngine engine, IClock clock) : IEnforcementNotifier
{
    internal static readonly TimeSpan BlockedCooldown = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan[] CountdownWarnings = [TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1)];

    private readonly ConcurrentDictionary<string, long> _lastShown = new();
    private RewardState? _lastState;
    private DateTimeOffset? _warnedActivation;
    private int _warningsShown;

    public void Start()
    {
        _lastState = engine.GetStatus().State;
        engine.StateChanged += (_, status) => OnRewardStateChanged(status);
    }

    public void OnLaunchBlocked(ApplicationRule rule)
    {
        var status = engine.GetStatus();
        var message = status.State switch
        {
            RewardState.RewardReady => "Your reward is ready. Unlock it from the Work to Unlock tray menu.",
            _ => $"Work {Format(status.RemainingWork)} more in your approved apps to unlock it.",
        };
        Show($"blocked:{rule.Id}", BlockedCooldown, $"{rule.Name} is locked", message);
    }

    public void OnClosingRestrictedApp(ApplicationRule rule, TimeSpan gracePeriod)
    {
        var message = settings.Current.EnforcementMode == EnforcementMode.CloseOnly
            ? $"{rule.Name} has been asked to close."
            : $"{rule.Name} will be closed in {Math.Max(1, (int)gracePeriod.TotalSeconds)} seconds. Save your progress now.";
        Show($"closing:{rule.Id}", BlockedCooldown, "Reward time is over", message);
    }

    public void NotifyDailyGoalReached(DailyProgress progress)
    {
        var streak = progress.CurrentStreak switch
        {
            <= 1 => "Come back tomorrow to start a streak.",
            var days => $"That's a {days}-day streak.",
        };
        Show($"goal:{clock.UtcNow:yyyyMMdd}", TimeSpan.Zero, "Daily goal reached!", $"{Format(progress.Goal)} of focused work today. {streak}");
    }

    public void NotifyAchievementsUnlocked(IReadOnlyList<AchievementDefinition> achievements)
    {
        if (achievements.Count == 1)
        {
            Show($"achievement:{achievements[0].Id}", TimeSpan.Zero, $"Achievement unlocked: {achievements[0].Title}", achievements[0].Description);
        }
        else if (achievements.Count > 1)
        {
            Show("achievements", TimeSpan.Zero, $"{achievements.Count} achievements unlocked", string.Join(", ", achievements.Select(a => a.Title)));
        }
    }

    /// <summary>Called by the main loop: warns before an active reward ends.</summary>
    public void CheckRewardCountdown(RewardStatus status)
    {
        if (status.State != RewardState.Unlocked || status.RewardActivatedUtc is null)
        {
            return;
        }

        if (_warnedActivation != status.RewardActivatedUtc)
        {
            _warnedActivation = status.RewardActivatedUtc;

            // Skip warnings that are already in the past (for example a short reward).
            _warningsShown = CountdownWarnings.Count(w => status.RemainingReward <= w);
        }

        while (_warningsShown < CountdownWarnings.Length && status.RemainingReward <= CountdownWarnings[_warningsShown])
        {
            var warning = CountdownWarnings[_warningsShown++];
            Show($"countdown:{warning}", TimeSpan.Zero, "Reward ending soon", $"Restricted apps will be locked again in {Format(warning)}.");
        }
    }

    private void OnRewardStateChanged(RewardStatus status)
    {
        var previous = _lastState;
        _lastState = status.State;
        if (previous == status.State)
        {
            return;
        }

        switch (status.State)
        {
            case RewardState.RewardReady:
                Show("ready", TimeSpan.Zero, "Reward earned!", $"You worked {Format(status.RequiredWork)}. Unlock your apps for {Format(status.RewardDuration)} whenever you like.");
                break;
            case RewardState.Unlocked:
                Show("unlocked", TimeSpan.Zero, "Apps unlocked", $"Restricted apps are available for {Format(status.RemainingReward)}.");
                break;
            case RewardState.Working when previous == RewardState.Unlocked:
                Show("ended", TimeSpan.Zero, "Reward ended", "Restricted apps are locked again. Time to get back to work.");
                break;
        }
    }

    private void Show(string key, TimeSpan cooldown, string title, string message)
    {
        if (!settings.Current.NotificationsEnabled)
        {
            return;
        }

        var now = clock.MonotonicMilliseconds;
        if (cooldown > TimeSpan.Zero && _lastShown.TryGetValue(key, out var last) && now - last < (long)cooldown.TotalMilliseconds)
        {
            return;
        }

        _lastShown[key] = now;
        tray.ShowNotification(title, message);
    }

    internal static string Format(TimeSpan time)
    {
        if (time.TotalMinutes < 1)
        {
            return $"{Math.Max(1, (int)Math.Ceiling(time.TotalSeconds))} sec";
        }

        var minutes = (int)Math.Ceiling(time.TotalMinutes);
        return minutes >= 60 ? $"{minutes / 60} h {minutes % 60:D2} min" : $"{minutes} min";
    }
}
