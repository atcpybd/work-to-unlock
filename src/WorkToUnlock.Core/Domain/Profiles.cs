namespace WorkToUnlock.Core.Domain;

/// <summary>
/// A named setup to switch to in one step, such as "School" (60/10, every game blocked) or "Weekend" (25/15, Discord
/// allowed): work and break times, how breaks are earned, the daily goal, and which restricted apps stay open.
/// </summary>
public sealed record Profile
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public int WorkRequirementMinutes { get; init; }

    public int RewardDurationMinutes { get; init; }

    public RewardMode RewardMode { get; init; }

    public int BalanceCapMinutes { get; init; }

    public int DailyGoalMinutes { get; init; }

    /// <summary>Restricted apps (rule ids) that are not blocked while this profile is in use.</summary>
    public IReadOnlyList<long> AllowedAppIds { get; init; } = [];
}

/// <summary>All profiles, plus the one the current settings and app rules match (if any).</summary>
public sealed record ProfileList(IReadOnlyList<Profile> Profiles, string? ActiveProfileId);

public static class Profiles
{
    public const int MaxProfiles = 12;
    public const int MaxNameLength = 40;

    /// <summary>The current setup as a profile. Restricted apps that are turned off count as allowed.</summary>
    public static Profile Capture(string id, string name, AppSettings settings, IEnumerable<ApplicationRule> rules)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(rules);
        return new Profile
        {
            Id = id,
            Name = name.Trim(),
            WorkRequirementMinutes = settings.WorkRequirementMinutes,
            RewardDurationMinutes = settings.RewardDurationMinutes,
            RewardMode = settings.RewardMode,
            BalanceCapMinutes = settings.BalanceCapMinutes,
            DailyGoalMinutes = settings.DailyGoalMinutes,
            AllowedAppIds = rules.Where(r => r.RuleType == RuleType.Restricted && !r.Enabled).Select(r => r.Id).Order().ToList(),
        };
    }

    public static AppSettings ApplyTo(AppSettings settings, Profile profile)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(profile);
        return settings with
        {
            WorkRequirementMinutes = profile.WorkRequirementMinutes,
            RewardDurationMinutes = profile.RewardDurationMinutes,
            RewardMode = profile.RewardMode,
            BalanceCapMinutes = profile.BalanceCapMinutes,
            DailyGoalMinutes = profile.DailyGoalMinutes,
        };
    }

    /// <summary>
    /// Restricted apps to turn on or off for the profile. Apps added after the profile was saved stay blocked.
    /// </summary>
    public static IReadOnlyList<ApplicationRule> RuleChanges(Profile profile, IEnumerable<ApplicationRule> rules)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(rules);
        return rules
            .Where(r => r.RuleType == RuleType.Restricted && r.Enabled == profile.AllowedAppIds.Contains(r.Id))
            .Select(r => r with { Enabled = !r.Enabled })
            .ToList();
    }

    public static bool Matches(Profile profile, AppSettings settings, IEnumerable<ApplicationRule> rules) =>
        ApplyTo(settings, profile) == settings && RuleChanges(profile, rules).Count == 0;

    public static IReadOnlyList<ValidationError> ValidateName(string? name, IReadOnlyCollection<Profile> existing)
    {
        ArgumentNullException.ThrowIfNull(existing);
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return [new(nameof(Profile.Name), "Enter a name for the profile.")];
        }

        if (trimmed.Length > MaxNameLength)
        {
            return [new(nameof(Profile.Name), $"Use at most {MaxNameLength} characters.")];
        }

        var replacing = existing.Any(p => string.Equals(p.Name, trimmed, StringComparison.OrdinalIgnoreCase));
        return !replacing && existing.Count >= MaxProfiles
            ? [new(nameof(Profile.Name), $"You can keep up to {MaxProfiles} profiles. Delete one first.")]
            : [];
    }
}
