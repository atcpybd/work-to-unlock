using System.Text.Json.Serialization;

namespace WorkToUnlock.Core.Domain;

public enum EnforcementMode
{
    /// <summary>Ask restricted apps to close; never terminate them.</summary>
    CloseOnly = 0,

    /// <summary>Ask restricted apps to close, then terminate them after the grace period.</summary>
    CloseThenTerminate = 1,

    /// <summary>
    /// Process enforcement plus AppLocker launch prevention managed by the optional elevated service.
    /// Must be enabled explicitly by an administrator.
    /// </summary>
    Strict = 2,
}

public enum ThemePreference
{
    System = 0,
    Light = 1,
    Dark = 2,
}

/// <summary>User-configurable settings. Persisted as key/value rows in the Settings table.</summary>
public sealed record AppSettings
{
    public int WorkRequirementMinutes { get; init; } = 60;

    public int RewardDurationMinutes { get; init; } = 20;

    public int IdleThresholdSeconds { get; init; } = 60;

    public bool StartWithWindows { get; init; } = true;

    public bool MinimizeToTray { get; init; } = true;

    public bool NotificationsEnabled { get; init; } = true;

    public ThemePreference Theme { get; init; } = ThemePreference.System;

    public EnforcementMode EnforcementMode { get; init; } = EnforcementMode.CloseThenTerminate;

    public int GracefulCloseTimeoutSeconds { get; init; } = 15;

    public int DataRetentionDays { get; init; } = 90;

    /// <summary>
    /// Master switch for enforcement. Only changed through the dedicated, audited
    /// "set enforcement enabled" command, never through a general settings update.
    /// </summary>
    public bool EnforcementEnabled { get; init; } = true;

    [JsonIgnore]
    public TimeSpan WorkRequirement => TimeSpan.FromMinutes(WorkRequirementMinutes);

    [JsonIgnore]
    public TimeSpan RewardDuration => TimeSpan.FromMinutes(RewardDurationMinutes);

    [JsonIgnore]
    public TimeSpan IdleThreshold => TimeSpan.FromSeconds(IdleThresholdSeconds);

    [JsonIgnore]
    public TimeSpan GracefulCloseTimeout => TimeSpan.FromSeconds(GracefulCloseTimeoutSeconds);

    public static AppSettings Default { get; } = new();
}

public sealed record ValidationError(string Property, string Message);

/// <summary>Validates settings and rejects values that would make tracking or enforcement unsafe.</summary>
public static class SettingsValidator
{
    public const int MinWorkMinutes = 1;
    public const int MaxWorkMinutes = 720;
    public const int MinRewardMinutes = 1;
    public const int MaxRewardMinutes = 240;
    public const int MinIdleSeconds = 10;
    public const int MaxIdleSeconds = 3600;
    public const int MinGraceSeconds = 3;
    public const int MaxGraceSeconds = 300;
    public const int MinRetentionDays = 7;
    public const int MaxRetentionDays = 3650;

    public static IReadOnlyList<ValidationError> Validate(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var errors = new List<ValidationError>();

        Range(errors, nameof(AppSettings.WorkRequirementMinutes), settings.WorkRequirementMinutes, MinWorkMinutes, MaxWorkMinutes, "Work requirement", "minutes");
        Range(errors, nameof(AppSettings.RewardDurationMinutes), settings.RewardDurationMinutes, MinRewardMinutes, MaxRewardMinutes, "Reward duration", "minutes");
        Range(errors, nameof(AppSettings.IdleThresholdSeconds), settings.IdleThresholdSeconds, MinIdleSeconds, MaxIdleSeconds, "Idle threshold", "seconds");
        Range(errors, nameof(AppSettings.GracefulCloseTimeoutSeconds), settings.GracefulCloseTimeoutSeconds, MinGraceSeconds, MaxGraceSeconds, "Graceful-close timeout", "seconds");
        Range(errors, nameof(AppSettings.DataRetentionDays), settings.DataRetentionDays, MinRetentionDays, MaxRetentionDays, "Data retention", "days");

        if (!Enum.IsDefined(settings.EnforcementMode))
        {
            errors.Add(new(nameof(AppSettings.EnforcementMode), "Unknown enforcement mode."));
        }

        if (!Enum.IsDefined(settings.Theme))
        {
            errors.Add(new(nameof(AppSettings.Theme), "Unknown theme."));
        }

        return errors;
    }

    private static void Range(List<ValidationError> errors, string property, int value, int min, int max, string label, string unit)
    {
        if (value < min || value > max)
        {
            errors.Add(new(property, $"{label} must be between {min} and {max} {unit}."));
        }
    }
}
