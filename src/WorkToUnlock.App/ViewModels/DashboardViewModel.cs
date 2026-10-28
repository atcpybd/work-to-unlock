using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using WorkToUnlock.App.Services;
using WorkToUnlock.Core.Activity;
using WorkToUnlock.Core.Domain;
using WorkToUnlock.Core.Enforcement;
using WorkToUnlock.Core.Ipc;

namespace WorkToUnlock.App.ViewModels;

public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly AgentConnection _agent;
    private readonly DialogService _dialogs;
    private readonly IconCache _icons;
    private IReadOnlyList<ApplicationRule> _rules = [];

    public DashboardViewModel(AgentConnection agent, DialogService dialogs, IconCache icons)
    {
        _agent = agent;
        _dialogs = dialogs;
        _icons = icons;
        agent.StatusChanged += (_, status) => Apply(status);
        agent.RulesChanged += (_, rules) => SetRules(rules);
        agent.ConnectionChanged += async (_, connected) =>
        {
            if (connected)
            {
                await LoadRulesAsync();
            }
        };
        if (agent.LastStatus is { } last)
        {
            Apply(last);
        }
    }

    public ObservableCollection<RestrictedAppStatusViewModel> RestrictedApps { get; } = [];

    public MetricViewModel ActivityMetric { get; } = new("Current activity");

    public MetricViewModel RemainingMetric { get; } = new("Work remaining") { Detail = "Only active time in approved apps counts." };

    public MetricViewModel RewardMetric { get; } = new("Reward") { Detail = "Access to restricted apps per completed goal." };

    public MetricViewModel EnforcementMetric { get; } = new("Enforcement");

    public IReadOnlyList<MetricViewModel> Metrics => [ActivityMetric, RemainingMetric, RewardMetric, EnforcementMetric];

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string WorkedText { get; set; } = "0 min";

    [ObservableProperty]
    public partial string RequiredText { get; set; } = "of 60 min";

    [ObservableProperty]
    public partial string Headline { get; set; } = "Connecting to Work to Unlock…";

    [ObservableProperty]
    public partial string Subtitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsReady { get; set; }

    [ObservableProperty]
    public partial bool IsUnlocked { get; set; }

    [ObservableProperty]
    public partial bool IsWorking { get; set; } = true;

    [ObservableProperty]
    public partial string UnlockButtonText { get; set; } = "Unlock Apps for 20 Minutes";

    [ObservableProperty]
    public partial string CountdownText { get; set; } = "00:00";

    [ObservableProperty]
    public partial string ExpiresText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ActivityTitle { get; set; } = "Waiting for activity";

    [ObservableProperty]
    public partial string ActivityDetail { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    [ObservableProperty]
    public partial string RemainingWorkText { get; set; } = "60 min";

    [ObservableProperty]
    public partial string RewardText { get; set; } = "20 min";

    [ObservableProperty]
    public partial string EnforcementTitle { get; set; } = "Unknown";

    [ObservableProperty]
    public partial string EnforcementDetail { get; set; } = string.Empty;

    [ObservableProperty]
    public partial InfoBarSeverity EnforcementSeverity { get; set; } = InfoBarSeverity.Informational;

    [ObservableProperty]
    public partial bool HasEnforcementProblems { get; set; }

    [ObservableProperty]
    public partial string EnforcementProblems { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool PersistenceWarning { get; set; }

    [ObservableProperty]
    public partial bool HasNoRestrictedApps { get; set; }

    [RelayCommand]
    private async Task ActivateAsync()
    {
        try
        {
            var result = await _agent.RequestAsync<ActivationResult>(AgentMethods.ActivateReward);
            if (result is { Succeeded: false })
            {
                await _dialogs.ShowMessageAsync("Reward not available", result.Outcome == ActivationOutcome.AlreadyActive
                    ? "A reward is already active."
                    : "Finish your work goal first.");
            }
            else if (result?.Status is { } status)
            {
                Apply(status);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Infrastructure.Ipc.IpcException or TimeoutException)
        {
            await _dialogs.ShowMessageAsync("Could not unlock", ex.Message);
        }
    }

    public async Task LoadRulesAsync()
    {
        try
        {
            SetRules(await _agent.RequestAsync<List<ApplicationRule>>(AgentMethods.ListRules) ?? []);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Infrastructure.Ipc.IpcException or TimeoutException)
        {
        }
    }

    private void Apply(AgentStatus status)
    {
        Apply(status.Reward);
        ApplyActivity(status);
        ApplyEnforcement(status);
        PersistenceWarning = !status.PersistenceHealthy;
        UpdateRestrictedStatuses(status);

        ActivityMetric.Value = ActivityTitle;
        ActivityMetric.Detail = ActivityDetail;
        ActivityMetric.ShowIndicator = IsActive;
        RemainingMetric.Value = RemainingWorkText;
        RewardMetric.Value = RewardText;
        EnforcementMetric.Value = EnforcementTitle;
        EnforcementMetric.Detail = EnforcementDetail;
    }

    private void Apply(RewardStatus reward)
    {
        Progress = reward.State == RewardState.Unlocked && reward.RewardDuration > TimeSpan.Zero
            ? reward.RemainingReward.TotalSeconds / reward.RewardDuration.TotalSeconds
            : reward.ProgressFraction;
        IsWorking = reward.State == RewardState.Working;
        IsReady = reward.State == RewardState.RewardReady;
        IsUnlocked = reward.State == RewardState.Unlocked;
        RewardText = Format.Duration(reward.RewardDuration);
        UnlockButtonText = $"Unlock Apps for {Format.LongDuration(reward.RewardDuration)}";
        RemainingWorkText = reward.State == RewardState.Working ? Format.DurationCeiling(reward.RemainingWork) : "Done";

        switch (reward.State)
        {
            case RewardState.RewardReady:
                WorkedText = Format.Duration(reward.RequiredWork);
                RequiredText = "goal reached";
                Headline = "Reward ready";
                Subtitle = $"You earned {Format.Duration(reward.RewardDuration)} with your restricted apps. Unlock it whenever you like; it does not expire.";
                break;
            case RewardState.Unlocked:
                WorkedText = Format.Countdown(reward.RemainingReward);
                RequiredText = "remaining";
                CountdownText = Format.Countdown(reward.RemainingReward);
                ExpiresText = $"Restricted apps lock again at {Format.LocalTime(reward.RewardExpiresUtc)}";
                Headline = "Apps unlocked";
                Subtitle = "Enjoy your break. The countdown keeps running even if you step away or the PC sleeps.";
                break;
            default:
                WorkedText = Format.Duration(reward.AccumulatedWork);
                RequiredText = $"of {Format.Duration(reward.RequiredWork)}";
                Headline = reward.AccumulatedWork == TimeSpan.Zero ? "Time to focus" : "Keep going";
                Subtitle = $"Work {Format.DurationCeiling(reward.RemainingWork)} more in your approved apps to unlock {Format.Duration(reward.RewardDuration)} of restricted apps.";
                break;
        }
    }

    private void ApplyActivity(AgentStatus status)
    {
        var tracking = status.Tracking;
        IsActive = tracking.Status == ActivityStatus.Working && status.Reward.State == RewardState.Working;
        (ActivityTitle, ActivityDetail) = tracking.Status switch
        {
            ActivityStatus.Working when status.Reward.State != RewardState.Working => (tracking.CurrentApplicationName ?? "Work app", "Active, but work does not count while a reward is ready or running"),
            ActivityStatus.Working => (tracking.CurrentApplicationName ?? "Work app", "Active: this time counts"),
            ActivityStatus.Idle => (tracking.CurrentApplicationName ?? "Idle", $"Idle for {Format.Duration(tracking.IdleDuration)}: paused"),
            ActivityStatus.NotWorkApp => ("Not a work app", "Switch to an approved app to keep earning"),
            ActivityStatus.Locked => ("PC locked", "Tracking is paused"),
            ActivityStatus.Disconnected => ("Session disconnected", "Tracking is paused"),
            ActivityStatus.Suspended => ("Sleeping", "Tracking is paused"),
            ActivityStatus.NoForeground => (tracking.CurrentApplicationName ?? "No active window", "Bring an approved app to the front"),
            _ => ("Starting…", string.Empty),
        };
    }

    private void ApplyEnforcement(AgentStatus status)
    {
        var health = status.Enforcement;
        if (!status.EnforcementEnabled)
        {
            EnforcementTitle = "Enforcement disabled";
            EnforcementSeverity = InfoBarSeverity.Warning;
        }
        else
        {
            (EnforcementTitle, EnforcementSeverity) = health.State switch
            {
                EnforcementHealthState.Healthy => (health.LaunchPreventionActive ? "Protected (strict)" : "Protected", InfoBarSeverity.Success),
                EnforcementHealthState.Degraded => ("Partly protected", InfoBarSeverity.Warning),
                EnforcementHealthState.Failed => ("Not protected", InfoBarSeverity.Error),
                _ => ("Not restricting", InfoBarSeverity.Informational),
            };
        }

        EnforcementDetail = health.Message;
        HasEnforcementProblems = health.Problems.Count > 0;
        EnforcementProblems = string.Join(Environment.NewLine, health.Problems);
    }

    private void SetRules(IReadOnlyList<ApplicationRule> rules)
    {
        _rules = rules;
        RestrictedApps.Clear();
        foreach (var rule in rules.Where(r => r.RuleType == RuleType.Restricted))
        {
            var item = new RestrictedAppStatusViewModel(rule);
            RestrictedApps.Add(item);
            _ = item.LoadIconAsync(_icons);
        }

        HasNoRestrictedApps = RestrictedApps.Count == 0;
        if (_agent.LastStatus is { } status)
        {
            UpdateRestrictedStatuses(status);
        }
    }

    private void UpdateRestrictedStatuses(AgentStatus status)
    {
        foreach (var item in RestrictedApps)
        {
            item.Update(status);
        }
    }
}

public sealed partial class MetricViewModel(string title) : ObservableObject
{
    public string Title { get; } = title;

    [ObservableProperty]
    public partial string Value { get; set; } = "–";

    [ObservableProperty]
    public partial string Detail { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ShowIndicator { get; set; }
}

public sealed partial class RestrictedAppStatusViewModel(ApplicationRule rule) : ObservableObject
{
    public string Name => rule.Name;

    [ObservableProperty]
    public partial Microsoft.UI.Xaml.Media.ImageSource? Icon { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Locked";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLocked))]
    public partial bool IsAvailable { get; set; }

    public bool IsLocked => !IsAvailable;

    public async Task LoadIconAsync(IconCache icons) => Icon = await icons.GetAsync(rule.ExecutablePath);

    public void Update(AgentStatus status)
    {
        IsAvailable = !rule.Enabled || !status.EnforcementEnabled || status.Reward.State == RewardState.Unlocked;
        StatusText = !rule.Enabled ? "Not restricted"
            : !status.EnforcementEnabled ? "Enforcement off"
            : status.Reward.State == RewardState.Unlocked ? "Available"
            : "Locked";
    }
}
