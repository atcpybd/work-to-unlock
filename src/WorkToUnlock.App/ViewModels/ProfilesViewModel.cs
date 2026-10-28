using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WorkToUnlock.App.Services;
using WorkToUnlock.Core.Domain;
using WorkToUnlock.Core.Ipc;

namespace WorkToUnlock.App.ViewModels;

/// <summary>Saved profiles, shared by the dashboard (switching) and Settings (saving and deleting).</summary>
public sealed partial class ProfilesViewModel : ObservableObject
{
    private readonly AgentConnection _agent;
    private readonly DialogService _dialogs;
    private IReadOnlyList<ApplicationRule> _rules = [];
    private int _loadVersion;

    public ProfilesViewModel(AgentConnection agent, DialogService dialogs)
    {
        _agent = agent;
        _dialogs = dialogs;

        // Which profile is in use follows the settings and app rules, so reload when either changes.
        agent.SettingsChanged += async (_, _) => await LoadAsync();
        agent.RulesChanged += async (_, rules) =>
        {
            _rules = rules;
            await LoadAsync();
        };
        agent.ConnectionChanged += async (_, connected) =>
        {
            if (connected)
            {
                await LoadAsync();
            }
        };
    }

    public ObservableCollection<ProfileItemViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial bool HasProfiles { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string NewName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ActiveText { get; set; } = string.Empty;

    public async Task LoadAsync()
    {
        if (!_agent.IsConnected)
        {
            return; // reloaded on connect
        }

        var version = ++_loadVersion;
        ProfileList? list;
        try
        {
            if (_rules.Count == 0)
            {
                _rules = await _agent.RequestAsync<List<ApplicationRule>>(AgentMethods.ListRules) ?? [];
            }

            list = await _agent.RequestAsync<ProfileList>(AgentMethods.ListProfiles);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Infrastructure.Ipc.IpcException or TimeoutException)
        {
            return;
        }

        // A newer load started while this one waited; let it fill the list.
        if (list is null || version != _loadVersion)
        {
            return;
        }

        Items.Clear();
        foreach (var profile in list.Profiles)
        {
            Items.Add(new ProfileItemViewModel(this, profile, profile.Id == list.ActiveProfileId, Summarize(profile)));
        }

        HasProfiles = Items.Count > 0;
        var active = list.Profiles.FirstOrDefault(p => p.Id == list.ActiveProfileId);
        ActiveText = active is null ? "Your current setup doesn't match a saved profile." : $"In use: {active.Name}";
    }

    private bool CanSave => !string.IsNullOrWhiteSpace(NewName);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        var name = NewName.Trim();
        if (Items.Any(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase))
            && !await _dialogs.ConfirmAsync("Replace profile?", $"'{name}' already exists. Replace it with your current setup?", "Replace"))
        {
            return;
        }

        if (await RequestAsync(AgentMethods.SaveProfile, new ProfileRequest(Name: name), "Profile not saved") is { Succeeded: true })
        {
            NewName = string.Empty;
        }
    }

    internal Task UseAsync(ProfileItemViewModel item) =>
        RequestAsync(AgentMethods.ApplyProfile, new ProfileRequest(Id: item.Profile.Id), "Profile not used");

    internal async Task DeleteAsync(ProfileItemViewModel item)
    {
        if (await _dialogs.ConfirmAsync("Delete profile?", $"'{item.Name}' is removed. Your current settings and apps don't change.", "Delete", destructive: true))
        {
            await RequestAsync(AgentMethods.DeleteProfile, new ProfileRequest(Id: item.Profile.Id), "Profile not deleted");
        }
    }

    private async Task<OperationResult?> RequestAsync(string method, ProfileRequest request, string failureTitle)
    {
        OperationResult? result;
        try
        {
            result = await _agent.RequestAsync<OperationResult>(method, request);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Infrastructure.Ipc.IpcException or TimeoutException)
        {
            await _dialogs.ShowMessageAsync(failureTitle, ex.Message);
            return null;
        }

        if (result is { Succeeded: false })
        {
            await _dialogs.ShowMessageAsync(failureTitle, result.Message ?? "The agent rejected the request.");
        }

        await LoadAsync();
        return result;
    }

    private string Summarize(Profile profile)
    {
        var allowed = _rules.Where(r => profile.AllowedAppIds.Contains(r.Id)).Select(r => r.Name).ToList();
        var parts = new List<string> { $"{profile.WorkRequirementMinutes} / {profile.RewardDurationMinutes} min" };
        if (profile.RewardMode == RewardMode.Balance)
        {
            parts.Add("earn as you go");
        }

        parts.Add(allowed.Count == 0 ? "all apps blocked" : $"{string.Join(", ", allowed)} allowed");
        return string.Join(" · ", parts);
    }
}

public sealed partial class ProfileItemViewModel(ProfilesViewModel owner, Profile profile, bool isActive, string summary) : ObservableObject
{
    internal Profile Profile => profile;

    public string Name => profile.Name;

    public string Summary => summary;

    public bool IsActive => isActive;

    public bool IsInactive => !isActive;

    public string AccessibleName => isActive ? $"{Name}, in use. {Summary}" : $"{Name}. {Summary}";

    [RelayCommand]
    private Task UseAsync() => owner.UseAsync(this);

    [RelayCommand]
    private Task DeleteAsync() => owner.DeleteAsync(this);
}
