using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WorkToUnlock.App.Services;
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

    private readonly MetricViewModel _today = new("Today") { Detail = "Qualifying work" };
    private readonly MetricViewModel _week = new("This week") { Detail = "Qualifying work" };
    private readonly MetricViewModel _earned = new("Rewards earned");
    private readonly MetricViewModel _rewardTime = new("Reward time activated") { Detail = "Total unlocked time granted" };

    public IReadOnlyList<MetricViewModel> Metrics => [_today, _week, _earned, _rewardTime];

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
        try
        {
            report = await agent.RequestAsync<StatisticsReport>(AgentMethods.GetStatistics);
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
                height));
        }
    }
}

public sealed record ApplicationBarViewModel(string Name, string TimeText, double Percent);

public sealed record DayBarViewModel(string Label, string DateText, string TimeText, double BarHeight)
{
    public string ToolTip => $"{DateText}: {TimeText}";
}
