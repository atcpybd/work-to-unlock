using System.Text.Json;
using Microsoft.Extensions.Logging;
using WorkToUnlock.Core.Domain;
using WorkToUnlock.Core.Ipc;
using WorkToUnlock.Core.Persistence;
using WorkToUnlock.Core.Rules;

namespace WorkToUnlock.Agent.Services;

/// <summary>
/// Saves and switches between profiles. Switching goes through the same validated, audited paths as changing the
/// settings and turning restricted apps on or off by hand.
/// </summary>
public sealed class ProfileService(
    ISettingsStore values,
    SettingsService settings,
    IRuleCatalog rules,
    IAuditLog audit,
    ILogger<ProfileService> logger)
{
    internal const string Key = "Profiles";
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<ProfileList> GetAsync(CancellationToken cancellationToken)
    {
        var all = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var active = all.FirstOrDefault(p => Profiles.Matches(p, settings.Current, rules.Rules));
        return new ProfileList(all, active?.Id);
    }

    /// <summary>Saves the current setup under this name, replacing a profile with the same name.</summary>
    public async Task<OperationResult> SaveCurrentAsync(string? name, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var all = (await LoadAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var errors = Profiles.ValidateName(name, all);
            if (errors.Count > 0)
            {
                return OperationResult.Fail(errors[0].Message, errors);
            }

            var trimmed = name!.Trim();
            var index = all.FindIndex(p => string.Equals(p.Name, trimmed, StringComparison.OrdinalIgnoreCase));
            var profile = Profiles.Capture(index >= 0 ? all[index].Id : Guid.NewGuid().ToString("N"), trimmed, settings.Current, rules.Rules);
            if (index >= 0)
            {
                all[index] = profile;
            }
            else
            {
                all.Add(profile);
            }

            await StoreAsync(all, cancellationToken).ConfigureAwait(false);
            await audit.WriteAsync("ProfileSaved", $"Profile '{trimmed}' saved: {Describe(profile)}.", "Success", cancellationToken).ConfigureAwait(false);
            return OperationResult.Ok(index >= 0 ? $"Profile '{trimmed}' updated." : $"Profile '{trimmed}' saved.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OperationResult> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var all = (await LoadAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var profile = all.Find(p => p.Id == id);
            if (profile is null)
            {
                return OperationResult.Fail("That profile no longer exists.");
            }

            all.Remove(profile);
            await StoreAsync(all, cancellationToken).ConfigureAwait(false);
            await audit.WriteAsync("ProfileDeleted", $"Profile '{profile.Name}' deleted.", "Success", cancellationToken).ConfigureAwait(false);
            return OperationResult.Ok($"Profile '{profile.Name}' deleted.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OperationResult> ApplyAsync(string id, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var profile = (await LoadAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(p => p.Id == id);
            if (profile is null)
            {
                return OperationResult.Fail("That profile no longer exists.");
            }

            var result = await settings.UpdateAsync(Profiles.ApplyTo(settings.Current, profile), cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                return result;
            }

            foreach (var rule in Profiles.RuleChanges(profile, rules.Rules))
            {
                await rules.SaveAsync(rule, cancellationToken).ConfigureAwait(false);
            }

            await audit.WriteAsync("ProfileApplied", $"Switched to profile '{profile.Name}': {Describe(profile)}.", "Success", cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Switched to profile {Name}", profile.Name);
            return OperationResult.Ok($"Switched to {profile.Name}.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private string Describe(Profile profile)
    {
        var allowed = rules.Rules.Where(r => profile.AllowedAppIds.Contains(r.Id)).Select(r => r.Name).ToList();
        return $"{profile.WorkRequirementMinutes} min work / {profile.RewardDurationMinutes} min break, "
            + (allowed.Count == 0 ? "all restricted apps blocked" : $"allowed: {string.Join(", ", allowed)}");
    }

    private async Task<IReadOnlyList<Profile>> LoadAsync(CancellationToken cancellationToken)
    {
        var json = await values.GetValueAsync(Key, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<Profile>>(json, IpcJson.Options) ?? [];
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Stored profiles are unreadable; starting with none");
            return [];
        }
    }

    private Task StoreAsync(IReadOnlyList<Profile> profiles, CancellationToken cancellationToken) =>
        values.SetValueAsync(Key, JsonSerializer.Serialize(profiles, IpcJson.Options), cancellationToken);
}
