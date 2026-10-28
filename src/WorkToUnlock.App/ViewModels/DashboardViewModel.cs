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
    private readonly PresetViewModel _customPreset;
    private IReadOnlyList<ApplicationRule> _rules = [];
    private AppSettings _settings = AppSettings.Default;

    public DashboardViewModel(AgentConnection agent, DialogService dialogs, IconCache icons)
    {
        _agent = agent;
        _dialogs = dialogs;
        _icons = icons;
        foreach (var preset in SessionPresets.All)
        {
            Presets.Add(new PresetViewModel(this, preset));
        }

        _customPreset = new PresetViewModel(this, null);
        Presets.Add(_customPreset);
        RefreshPresets();

        agent.StatusChanged += (_, status) => Apply(status);
        agent.RulesChanged += (_, rules) => SetRules(rules);
        agent.SettingsChanged += (_, settings) =>
        {
            _settings = settings;
            RefreshPresets();
        };
        agent.ConnectionChanged += async (_, connected) =>
        {
            if (connected)
            {
                await LoadRulesAsync();
                await LoadSettingsAsync();
                await LoadProgressAsync();
            }
        };
        if (agent.LastStatus is { } last)
        {
            Apply(last);
        }
    }

    public ObservableCollection<RestrictedAppStatusViewModel> RestrictedApps { get; } = [];

    /// <summary>One-click work/break presets plus a "Custom" choice.</summary>
    public ObservableCollection<PresetViewModel> Presets { get; } = [];

    [ObservableProperty]
    public partial string PresetSummary { get; set; } = string.Empty;

    /// <summary>Shows the custom work/break dialog. Returns null when cancelled.</summary>
    public Func<int, int, Task<(int WorkMinutes, int BreakMinutes)?>>? AskCustomTimes { get; set; }

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

    [ObservableProperty]
    public partial string TodayText { get; set; } = "–";

    [ObservableProperty]
    public partial double GoalPercent { get; set; }

    [ObservableProperty]
    public partial bool HasGoal { get; set; }

    [ObservableProperty]
    public partial string GoalStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StreakText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string BlockedTodayText { get; set; } = string.Empty;

    /// <summary>Refreshes today's goal progress, streak and blocked attempts from the agent.</summary>
    public async Task LoadProgressAsync()
    {
        if (!_agent.IsConnected)
        {
            return;
        }

        try
        {
            if (await _agent.RequestAsync<DailyProgress>(AgentMethods.GetDailyProgress) is { } progress)
            {
                ApplyProgress(progress);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Infrastructure.Ipc.IpcException or TimeoutException)
        {
        }
    }

    private void ApplyProgress(DailyProgress progress)
    {
        HasGoal = progress.GoalEnabled;
        GoalPercent = progress.Fraction * 100;
        TodayText = progress.GoalEnabled
            ? $"{Format.Duration(progress.Today)} of {Format.Duration(progress.Goal)}"
            : $"{Format.Duration(progress.Today)} of work";
        GoalStatusText = !progress.GoalEnabled ? "No daily goal set. You can add one in Settings."
            : progress.GoalMet ? "Daily goal reached. Nice work!"
            : $"{Format.DurationCeiling(progress.Goal - progress.Today)} to go for today's goal";
        StreakText = !progress.GoalEnabled ? "No streak without a daily goal"
            : progress.CurrentStreak == 0 ? (progress.BestStreak == 0 ? "No streak yet" : $"No streak right now (best {progress.BestStreak})")
            : $"{progress.CurrentStreak}-day streak (best {progress.BestStreak})";
        BlockedTodayText = progress.BlockedToday switch
        {
            0 => "No blocked attempts today",
            1 => "1 blocked attempt today",
            var n => $"{n} blocked attempts today",
        };
    }

    /// <summary>True when there is something to reset: work time so far, a ready break, or a running break.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResetCommand))]
    public partial bool CanReset { get; set; }

    [RelayCommand(CanExecute = nameof(CanReset))]
    private async Task ResetAsync()
    {
        var reward = _agent.LastStatus?.Reward;
        var (title, message) = reward?.State switch
        {
            RewardState.RewardReady => ("Reset and give up your break?",
                $"Your earned {Format.Duration(reward.RewardDuration)} break is discarded and the work timer starts again from 00:00."),
            RewardState.Unlocked => ("End your break and reset?",
                "Your break ends now, restricted apps are locked again, and the work timer starts from 00:00."),
            _ => ("Reset the work timer?",
                $"Your {Format.Countdown(reward?.AccumulatedWork ?? TimeSpan.Zero)} of work so far goes back to 00:00."),
        };

        if (!await _dialogs.ConfirmAsync(title, message + " This is recorded in the activity log.", "Reset", destructive: true))
        {
            return;
        }

        try
        {
            await _agent.RequestAsync<OperationResult>(AgentMethods.ResetProgress, new ResetProgressRequest("Reset from the dashboard"));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Infrastructure.Ipc.IpcException or TimeoutException)
        {
            await _dialogs.ShowMessageAsync("Could not reset", ex.Message);
        }
    }

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

    public async Task LoadSettingsAsync()
    {
        try
        {
            if (await _agent.RequestAsync<AppSettings>(AgentMethods.GetSettings) is { } settings)
            {
                _settings = settings;
                RefreshPresets();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Infrastructure.Ipc.IpcException or TimeoutException)
        {
        }
    }

    internal async Task SelectPresetAsync(PresetViewModel item)
    {
        if (item.Preset is { } preset)
        {
            await ApplyTimesAsync(preset.WorkMinutes, preset.BreakMinutes);
            return;
        }

        var custom = AskCustomTimes is null ? null : await AskCustomTimes(_settings.WorkRequirementMinutes, _settings.RewardDurationMinutes);
        if (custom is { } times)
        {
            await ApplyTimesAsync(times.WorkMinutes, times.BreakMinutes);
        }
        else
        {
            RefreshPresets();
        }
    }

    /// <summary>Changes only the work and break times, starting from the agent's latest settings.</summary>
    private async Task ApplyTimesAsync(int workMinutes, int breakMinutes)
    {
        try
        {
            var latest = await _agent.RequestAsync<AppSettings>(AgentMethods.GetSettings) ?? _settings;
            var requested = SessionPresets.Apply(latest, workMinutes, breakMinutes);
            if (requested == latest)
            {
                _settings = latest;
                return;
            }

            var errors = SettingsValidator.Validate(requested);
            if (errors.Count > 0)
            {
                await _dialogs.ShowMessageAsync("Times not changed", string.Join(Environment.NewLine, errors.Select(e => e.Message)));
                return;
            }

            var result = await _agent.RequestAsync<OperationResult>(AgentMethods.UpdateSettings, requested);
            if (result is { Succeeded: true })
            {
                _settings = requested;
            }
            else
            {
                await _dialogs.ShowMessageAsync("Times not changed", result?.Message ?? "No response from the agent.");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Infrastructure.Ipc.IpcException or TimeoutException)
        {
            await _dialogs.ShowMessageAsync("Times not changed", ex.Message);
        }
        finally
        {
            RefreshPresets();
        }
    }

    private void RefreshPresets()
    {
        var match = SessionPresets.Match(_settings);
        foreach (var item in Presets)
        {
            item.SetSelected(item.IsCustom ? match is null : item.Preset == match);
        }

        var work = _settings.WorkRequirementMinutes;
        var rest = _settings.RewardDurationMinutes;
        _customPreset.Title = match is null ? $"{work} / {rest}" : "Custom";
        _customPreset.Subtitle = match is null ? "Custom" : "Set your own";
        PresetSummary = $"{Format.Duration(_settings.WorkRequirement)} of work earns {Format.Duration(_settings.RewardDuration)} of break. "
            + "Changes apply right away; a break that is already running keeps its length.";
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
        CanReset = reward.State != RewardState.Working || reward.AccumulatedWork > TimeSpan.Zero;
        RewardText = Format.Duration(reward.RewardDuration);
        UnlockButtonText = $"Unlock Apps for {Format.LongDuration(reward.RewardDuration)}";
        // While working, show minutes and seconds so progress visibly ticks with each per-second status update.
        RemainingWorkText = reward.State == RewardState.Working ? Format.Countdown(reward.RemainingWork) : "Done";

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
                WorkedText = Format.Countdown(reward.AccumulatedWork);
                RequiredText = $"of {Format.Duration(reward.RequiredWork)}";
                Headline = reward.AccumulatedWork == TimeSpan.Zero ? "Time to focus" : "Keep going";
                Subtitle = $"Work {Format.DurationCeiling(reward.RemainingWork)} more in your approved apps to unlock {Format.Duration(reward.RewardDuration)} of restricted apps.";
                break;
        }
    }

    private void ApplyActivity(AgentStatus status)
    {
        var tracking = status.Tracking;

        // Time since the last input is held back by the agent; after a few seconds, say so instead of looking active.
        var waitingForInput = tracking.Status == ActivityStatus.Working && tracking.HeldWork >= TimeSpan.FromSeconds(5);
        IsActive = tracking.Status == ActivityStatus.Working && status.Reward.State == RewardState.Working && !waitingForInput;
        (ActivityTitle, ActivityDetail) = tracking.Status switch
        {
            ActivityStatus.Working when status.Reward.State != RewardState.Working => (tracking.CurrentApplicationName ?? "Work app", "Active, but work does not count while a reward is ready or running"),
            ActivityStatus.Working when waitingForInput => (tracking.CurrentApplicationName ?? "Work app",
                $"No input for {(int)tracking.IdleDuration.TotalSeconds} s: added when you continue, dropped after {_settings.IdleThresholdSeconds} s"),
            ActivityStatus.Working => (tracking.CurrentApplicationName ?? "Work app", "Active: this time counts"),
            ActivityStatus.Idle => (tracking.CurrentApplicationName ?? "Idle", $"Away for {Format.Duration(tracking.IdleDuration)}: not counted"),
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

/// <summary>A work/break preset button on the dashboard. A null preset is the "Custom" choice.</summary>
public sealed partial class PresetViewModel : ObservableObject
{
    private readonly DashboardViewModel _owner;

    public PresetViewModel(DashboardViewModel owner, SessionPreset? preset)
    {
        _owner = owner;
        Preset = preset;
        Title = preset is null ? "Custom" : $"{preset.WorkMinutes} / {preset.BreakMinutes}";
        Subtitle = preset?.Name ?? "Set your own";
    }

    public SessionPreset? Preset { get; }

    public bool IsCustom => Preset is null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    public partial string Title { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    public partial string Subtitle { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public string AccessibleName => Preset is { } p
        ? $"{p.Name}: {p.WorkMinutes} minutes of work, {p.BreakMinutes} minutes of break"
        : $"Custom work and break times ({Title})";

    /// <summary>
    /// Sets the selection and always notifies, so a toggle button the user just clicked snaps back to the real state
    /// even when the value did not change.
    /// </summary>
    internal void SetSelected(bool selected)
    {
        IsSelected = selected;
        OnPropertyChanged(nameof(IsSelected));
    }

    [RelayCommand]
    private Task SelectAsync() => _owner.SelectPresetAsync(this);
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
