using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WorkToUnlock.App.Services;
using WorkToUnlock.Core.Domain;
using WorkToUnlock.Core.Ipc;

namespace WorkToUnlock.App.ViewModels;

public sealed partial class ApplicationsViewModel : ObservableObject
{
    private readonly AgentConnection _agent;
    private readonly DialogService _dialogs;
    private readonly PickerService _pickers;
    private readonly IconCache _icons;
    private Dictionary<long, RuleTime> _times = [];

    public ApplicationsViewModel(AgentConnection agent, DialogService dialogs, PickerService pickers, IconCache icons)
    {
        _agent = agent;
        _dialogs = dialogs;
        _pickers = pickers;
        _icons = icons;
        agent.RulesChanged += (_, rules) => SetRules(rules);
        agent.StatusChanged += (_, status) =>
        {
            foreach (var item in RestrictedApps)
            {
                item.UpdateRestriction(status);
            }
        };
        agent.ConnectionChanged += async (_, connected) =>
        {
            if (connected)
            {
                await LoadAsync();
            }
        };
    }

    public ObservableCollection<RuleItemViewModel> WorkApps { get; } = [];

    public ObservableCollection<RuleItemViewModel> RestrictedApps { get; } = [];

    [ObservableProperty]
    public partial bool HasNoWorkApps { get; set; }

    [ObservableProperty]
    public partial bool HasNoRestrictedApps { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>Raised to let the view show the add/confirm dialog for an inspected executable.</summary>
    public Func<ExecutableInspection, RuleType, Task<(bool Confirmed, string Name, bool CountsTowardGoal)>>? ConfirmAdd { get; set; }

    /// <summary>Raised to let the view show the installed-apps picker.</summary>
    public Func<IReadOnlyList<InstalledApplication>, Task<string?>>? ChooseInstalledApp { get; set; }

    public async Task LoadAsync()
    {
        if (!_agent.IsConnected)
        {
            return; // reloaded on connect
        }

        await RunAsync(async () =>
        {
            var times = await _agent.RequestAsync<List<RuleTime>>(AgentMethods.GetRuleTimes) ?? [];
            _times = times.ToDictionary(t => t.RuleId);
            SetRules(await _agent.RequestAsync<List<ApplicationRule>>(AgentMethods.ListRules) ?? []);
        });
    }

    [RelayCommand]
    private Task BrowseWorkAppAsync() => BrowseAsync(RuleType.Work);

    [RelayCommand]
    private Task BrowseRestrictedAppAsync() => BrowseAsync(RuleType.Restricted);

    [RelayCommand]
    private Task ChooseInstalledWorkAppAsync() => ChooseInstalledAsync(RuleType.Work);

    [RelayCommand]
    private Task ChooseInstalledRestrictedAppAsync() => ChooseInstalledAsync(RuleType.Restricted);

    public Task SetEnabledAsync(RuleItemViewModel item, bool enabled) =>
        SaveAsync(item.Rule with { Enabled = enabled });

    public Task SetCountsTowardGoalAsync(RuleItemViewModel item, bool counts) =>
        SaveAsync(item.Rule with { CountsTowardGoal = counts });

    public async Task RevalidateAsync(RuleItemViewModel item)
    {
        await RunAsync(async () =>
        {
            var result = await _agent.RequestAsync<SaveRuleResult>(AgentMethods.RevalidateRule, new IdRequest(item.Rule.Id));
            if (result is { Succeeded: true, Rule: { } rule })
            {
                await _dialogs.ShowMessageAsync("Identity updated", $"{rule.Name} was re-checked.\n\nPath: {rule.ExecutablePath}\nPublisher: {rule.Identity.Publisher ?? "not signed"}");
            }
            else if (result is not null)
            {
                await _dialogs.ShowMessageAsync("Could not revalidate", string.Join(Environment.NewLine, result.Errors.Select(e => e.Message)));
            }
        });
    }

    public async Task ChangeLocationAsync(RuleItemViewModel item)
    {
        var path = await _pickers.PickExecutableAsync();
        if (path is null)
        {
            return;
        }

        await RunAsync(async () =>
        {
            var inspection = await _agent.RequestAsync<ExecutableInspection>(AgentMethods.InspectExecutable, new PathRequest(path, item.Rule.RuleType));
            if (inspection is null || !inspection.Exists)
            {
                await _dialogs.ShowMessageAsync("Could not use this file", string.Join(Environment.NewLine, inspection?.Problems.Select(p => p.Message) ?? []));
                return;
            }

            await SaveAsync(item.Rule with { ExecutablePath = inspection.ExecutablePath, Identity = inspection.Identity });
        });
    }

    public async Task RemoveAsync(RuleItemViewModel item)
    {
        var restricted = item.Rule.RuleType == RuleType.Restricted;
        if (!await _dialogs.ConfirmAsync($"Remove {item.Name}?",
                restricted ? "It will no longer be restricted." : "Time in this app will no longer count as work. Past statistics are kept.",
                "Remove", destructive: true))
        {
            return;
        }

        await RunAsync(async () =>
        {
            var result = await _agent.RequestAsync<OperationResult>(AgentMethods.DeleteRule, new IdRequest(item.Rule.Id));
            if (result is { Succeeded: false })
            {
                await _dialogs.ShowMessageAsync("Could not remove", result.Message ?? string.Empty);
            }
        });
    }

    private async Task BrowseAsync(RuleType type)
    {
        var path = await _pickers.PickExecutableAsync();
        if (path is not null)
        {
            await InspectAndAddAsync(path, type);
        }
    }

    private async Task ChooseInstalledAsync(RuleType type)
    {
        IReadOnlyList<InstalledApplication> apps = [];
        await RunAsync(async () => apps = await _agent.RequestAsync<List<InstalledApplication>>(AgentMethods.ListInstalledApps, timeout: TimeSpan.FromMinutes(1)) ?? []);
        if (ChooseInstalledApp is null)
        {
            return;
        }

        var path = await ChooseInstalledApp(apps);
        if (path is not null)
        {
            await InspectAndAddAsync(path, type);
        }
    }

    private async Task InspectAndAddAsync(string path, RuleType type)
    {
        ExecutableInspection? inspection = null;
        await RunAsync(async () => inspection = await _agent.RequestAsync<ExecutableInspection>(AgentMethods.InspectExecutable, new PathRequest(path, type), TimeSpan.FromMinutes(2)));
        if (inspection is null || ConfirmAdd is null)
        {
            return;
        }

        var (confirmed, name, counts) = await ConfirmAdd(inspection, type);
        if (!confirmed)
        {
            return;
        }

        await SaveAsync(new ApplicationRule
        {
            Name = string.IsNullOrWhiteSpace(name) ? inspection.SuggestedName : name.Trim(),
            ExecutablePath = inspection.ExecutablePath,
            Identity = inspection.Identity,
            RuleType = type,
            CountsTowardGoal = counts,
        });
    }

    private async Task SaveAsync(ApplicationRule rule)
    {
        await RunAsync(async () =>
        {
            var result = await _agent.RequestAsync<SaveRuleResult>(AgentMethods.SaveRule, rule);
            if (result is { Succeeded: false })
            {
                await _dialogs.ShowMessageAsync("Could not save", string.Join(Environment.NewLine, result.Errors.Select(e => e.Message)));
                SetRules(await _agent.RequestAsync<List<ApplicationRule>>(AgentMethods.ListRules) ?? []);
            }
        });
    }

    private void SetRules(IReadOnlyList<ApplicationRule> rules)
    {
        Sync(WorkApps, rules.Where(r => r.RuleType == RuleType.Work));
        Sync(RestrictedApps, rules.Where(r => r.RuleType == RuleType.Restricted));
        HasNoWorkApps = WorkApps.Count == 0;
        HasNoRestrictedApps = RestrictedApps.Count == 0;
        if (_agent.LastStatus is { } status)
        {
            foreach (var item in RestrictedApps)
            {
                item.UpdateRestriction(status);
            }
        }
    }

    private void Sync(ObservableCollection<RuleItemViewModel> target, IEnumerable<ApplicationRule> rules)
    {
        target.Clear();
        foreach (var rule in rules.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var item = new RuleItemViewModel(rule, this, _times.GetValueOrDefault(rule.Id));
            target.Add(item);
            _ = item.LoadIconAsync(_icons);
        }
    }

    private async Task RunAsync(Func<Task> action)
    {
        IsBusy = true;
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Infrastructure.Ipc.IpcException or TimeoutException)
        {
            await _dialogs.ShowMessageAsync("Something went wrong", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }
}

public sealed partial class RuleItemViewModel : ObservableObject
{
    private readonly ApplicationsViewModel _owner;
    private bool _syncing;

    public RuleItemViewModel(ApplicationRule rule, ApplicationsViewModel owner, RuleTime? time)
    {
        Rule = rule;
        _owner = owner;
        _syncing = true;
        Enabled = rule.Enabled;
        CountsTowardGoal = rule.CountsTowardGoal;
        _syncing = false;
        TodayText = time is null ? "No time today" : $"Today: {Format.Duration(time.Today)} · This week: {Format.Duration(time.ThisWeek)}";
        Missing = !File.Exists(rule.ExecutablePath);
    }

    public ApplicationRule Rule { get; }

    public string Name => Rule.Name;

    public string ExecutablePath => Rule.ExecutablePath;

    public bool IsWork => Rule.RuleType == RuleType.Work;

    public string Publisher => Rule.Identity.Publisher is { } publisher ? $"Signed by {publisher}" : "Not signed";

    public string ScopeText => Rule.Identity.Scope switch
    {
        MatchScope.InstallFolder => $"Install folder ({Rule.Identity.RelatedExecutables.Count + 1} known executables)",
        MatchScope.PublisherAndFileName => "Same file signed by the same publisher",
        _ => "This exact file",
    };

    public string TodayText { get; }

    public bool Missing { get; }

    [ObservableProperty]
    public partial Microsoft.UI.Xaml.Media.ImageSource? Icon { get; set; }

    [ObservableProperty]
    public partial bool Enabled { get; set; }

    [ObservableProperty]
    public partial bool CountsTowardGoal { get; set; }

    [ObservableProperty]
    public partial string RestrictionText { get; set; } = string.Empty;

    public async Task LoadIconAsync(IconCache icons) => Icon = await icons.GetAsync(Rule.ExecutablePath);

    public void UpdateRestriction(AgentStatus status) => RestrictionText = !Enabled ? "Restriction off"
        : !status.EnforcementEnabled ? "Enforcement disabled"
        : status.Reward.State == RewardState.Unlocked ? "Available now"
        : "Locked";

    partial void OnEnabledChanged(bool value)
    {
        if (!_syncing && value != Rule.Enabled)
        {
            _ = _owner.SetEnabledAsync(this, value);
        }
    }

    partial void OnCountsTowardGoalChanged(bool value)
    {
        if (!_syncing && value != Rule.CountsTowardGoal)
        {
            _ = _owner.SetCountsTowardGoalAsync(this, value);
        }
    }

    [RelayCommand]
    private Task RevalidateAsync() => _owner.RevalidateAsync(this);

    [RelayCommand]
    private Task ChangeLocationAsync() => _owner.ChangeLocationAsync(this);

    [RelayCommand]
    private Task RemoveAsync() => _owner.RemoveAsync(this);
}
