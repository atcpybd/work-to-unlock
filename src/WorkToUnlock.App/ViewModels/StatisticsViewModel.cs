using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WorkToUnlock.App.Services;
using WorkToUnlock.Core.Domain;
using WorkToUnlock.Core.Ipc;
using WorkToUnlock.Core.Persistence;

namespace WorkToUnlock.App.ViewModels;

public sealed partial class StatisticsViewModel : ObservableObject
{
    private const double BarAreaHeight = 140;
    private readonly AgentConnection agent;

    public StatisticsViewModel(AgentConnection agent)
    {
        this.agent = agent;

        // The page may open before the agent connection is up; load as soon as it is.
        agent.ConnectionChanged += async (_, connected) =>
        {
            if (connected)
            {
                await LoadAsync();
            }
        };
    }

    public ObservableCollection<ApplicationBarViewModel> Applications { get; } = [];

    public ObservableCollection<DayBarViewModel> Days { get; } = [];

    /// <summary>Newest first, at most <see cref="MaxIncidents"/>.</summary>
    public ObservableCollection<IncidentViewModel> Incidents { get; } = [];

    private const int MaxIncidents = 6;

    public ObservableCollection<AchievementViewModel> Achievements { get; } = [];

    [ObservableProperty]
    public partial string AchievementsSummary { get; set; } = string.Empty;

    private readonly MetricViewModel _today = new("Today") { Detail = "Qualifying work" };
    private readonly MetricViewModel _week = new("This week") { Detail = "Qualifying work" };
    private readonly MetricViewModel _earned = new("Rewards earned");
    private readonly MetricViewModel _rewardTime = new("Reward time activated") { Detail = "Total unlocked time granted" };
    private readonly MetricViewModel _blocked = new("Blocked today");
    private readonly MetricViewModel _streak = new("Streak");

    public IReadOnlyList<MetricViewModel> Metrics => [_today, _week, _streak, _blocked, _earned, _rewardTime];

    [ObservableProperty]
    public partial string TodayText { get; set; } = "–";

    [ObservableProperty]
    public partial string WeekText { get; set; } = "–";

    [ObservableProperty]
    public partial string RewardsEarnedText { get; set; } = "–";

    [ObservableProperty]
    public partial string RewardMinutesText { get; set; } = "–";

    [ObservableProperty]
    public partial string RewardsActivatedText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasNoApplications { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HonestyHasIssues))]
    public partial bool HonestyIsClean { get; set; } = true;

    public bool HonestyHasIssues => !HonestyIsClean;

    [ObservableProperty]
    public partial string HonestyHeadline { get; set; } = "–";

    [ObservableProperty]
    public partial string AgentStopsText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EnforcementOffText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool EnforcementOffNow { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (!agent.IsConnected)
        {
            return; // reloaded on connect
        }

        StatisticsReport? report;
        DailyProgress? progress;
        TamperReport? honesty;
        List<AchievementInfo>? achievements;
        try
        {
            report = await agent.RequestAsync<StatisticsReport>(AgentMethods.GetStatistics);
            progress = await agent.RequestAsync<DailyProgress>(AgentMethods.GetDailyProgress);
            honesty = await agent.RequestAsync<TamperReport>(AgentMethods.GetTamperReport);
            achievements = await agent.RequestAsync<List<AchievementInfo>>(AgentMethods.GetAchievements);
            ErrorText = null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Infrastructure.Ipc.IpcException or TimeoutException)
        {
            ErrorText = ex.Message;
            return;
        }

        if (report is null)
        {
            return;
        }

        TodayText = Format.Duration(report.WorkToday);
        WeekText = Format.Duration(report.WorkThisWeek);
        RewardsEarnedText = report.RewardsEarned.ToString(CultureInfo.CurrentCulture);
        RewardMinutesText = Format.Duration(report.RewardTimeActivated);
        RewardsActivatedText = $"{report.RewardsActivated} activated";
        _today.Value = TodayText;
        _week.Value = WeekText;
        _earned.Value = RewardsEarnedText;
        _earned.Detail = RewardsActivatedText;
        _rewardTime.Value = RewardMinutesText;
        var goal = progress?.Goal ?? TimeSpan.Zero;
        _streak.Value = progress is { GoalEnabled: true } ? (progress.CurrentStreak == 1 ? "1 day" : $"{progress.CurrentStreak} days") : "Off";
        _streak.Detail = progress is { GoalEnabled: true }
            ? $"Best {progress.BestStreak} · goal {Format.Duration(goal)} a day"
            : "Set a daily goal in Settings to build a streak";
        _blocked.Value = report.BlockedToday == 1 ? "1 attempt" : $"{report.BlockedToday} attempts";
        _blocked.Detail = report.BlockedToday == 0
            ? "Nothing blocked today"
            : string.Join(" · ", report.BlockedTodayByApplication.Select(a => $"{a.Name} {a.Count}"));

        Applications.Clear();
        var max = report.WorkByApplicationThisWeek.Select(a => a.Time.TotalMinutes).DefaultIfEmpty(0).Max();
        foreach (var app in report.WorkByApplicationThisWeek)
        {
            Applications.Add(new ApplicationBarViewModel(app.Name, Format.Duration(app.Time), max <= 0 ? 0 : app.Time.TotalMinutes / max * 100));
        }

        HasNoApplications = Applications.Count == 0;

        Days.Clear();
        var maxDay = report.DailyHistory.Select(d => d.QualifyingWork.TotalMinutes).DefaultIfEmpty(0).Max();
        foreach (var day in report.DailyHistory)
        {
            var height = maxDay <= 0 ? 2 : Math.Max(2, day.QualifyingWork.TotalMinutes / maxDay * BarAreaHeight);
            Days.Add(new DayBarViewModel(
                day.Date.ToString("ddd", CultureInfo.CurrentCulture)[..Math.Min(2, day.Date.ToString("ddd", CultureInfo.CurrentCulture).Length)],
                day.Date.ToString("d", CultureInfo.CurrentCulture),
                Format.Duration(day.QualifyingWork),
                height,
                day.BlockedAttempts,
                goal > TimeSpan.Zero && day.QualifyingWork >= goal));
        }

        if (honesty is not null)
        {
            ApplyHonesty(honesty);
        }

        if (achievements is not null)
        {
            Achievements.Clear();
            foreach (var achievement in achievements)
            {
                Achievements.Add(new AchievementViewModel(
                    achievement.Title,
                    achievement.Description,
                    achievement.Glyph,
                    achievement.UnlockedUtc is not null,
                    achievement.UnlockedUtc is { } when ? $"Unlocked {when.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture)}" : "Locked"));
            }

            AchievementsSummary = $"{achievements.Count(a => a.UnlockedUtc is not null)} of {achievements.Count} unlocked";
        }
    }

    private void ApplyHonesty(TamperReport report)
    {
        static string Times(int count) => count == 1 ? "once" : $"{count} times";

        HonestyIsClean = report.IsClean;
        var incidents = report.UnexpectedStops + report.TimesEnforcementDisabled;
        HonestyHeadline = report.IsClean
            ? $"Protected for the last {report.Days} days"
            : $"Unprotected {Times(incidents)} in the last {report.Days} days";
        AgentStopsText = report.UnexpectedStops == 0
            ? "Work to Unlock kept running"
            : $"Work to Unlock was closed or ended {Times(report.UnexpectedStops)} · {Format.Duration(report.TimeNotRunning)} not running";
        EnforcementOffText = report.TimesEnforcementDisabled == 0
            ? "Enforcement stayed on"
            : $"Enforcement turned off {Times(report.TimesEnforcementDisabled)} · {Format.Duration(report.TimeEnforcementDisabled)} in total";
        EnforcementOffNow = report.EnforcementDisabledNow;

        Incidents.Clear();
        foreach (var incident in report.Incidents.Take(MaxIncidents))
        {
            var local = incident.StartUtc.ToLocalTime();
            Incidents.Add(new IncidentViewModel(
                $"{local.ToString("ddd d MMM", CultureInfo.CurrentCulture)}, {local.ToString("t", CultureInfo.CurrentCulture)}",
                incident.Kind == DowntimeKinds.AgentNotRunning ? "Not running" : "Enforcement off",
                incident.EndUtc is null ? $"{Format.Duration(incident.Duration)} so far" : Format.Duration(incident.Duration)));
        }
    }
}

public sealed record IncidentViewModel(string WhenText, string KindText, string DurationText);

public sealed record AchievementViewModel(string Title, string Description, string Glyph, bool Unlocked, string StatusText)
{
    public bool Locked => !Unlocked;

    public double Opacity => Unlocked ? 1 : 0.55;

    public string ToolTip => $"{Title}: {Description}. {StatusText}";
}

public sealed record ApplicationBarViewModel(string Name, string TimeText, double Percent);

public sealed record DayBarViewModel(string Label, string DateText, string TimeText, double BarHeight, int BlockedAttempts, bool GoalMet)
{
    public bool GoalMissed => !GoalMet;

    public string ToolTip => (GoalMet ? $"{DateText}: {TimeText}, goal reached" : $"{DateText}: {TimeText}")
        + (BlockedAttempts == 0 ? string.Empty : $", {BlockedAttempts} blocked {(BlockedAttempts == 1 ? "attempt" : "attempts")}");
}
