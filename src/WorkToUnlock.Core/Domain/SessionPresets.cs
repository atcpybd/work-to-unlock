namespace WorkToUnlock.Core.Domain;

/// <summary>A named work-time / break-time pair offered as a one-click choice on the dashboard.</summary>
public sealed record SessionPreset(string Name, int WorkMinutes, int BreakMinutes);

public static class SessionPresets
{
    public static IReadOnlyList<SessionPreset> All { get; } =
    [
        new("Pomodoro", 25, 5),
        new("Focus", 50, 10),
        new("Standard", 60, 20),
        new("Deep work", 90, 30),
    ];

    /// <summary>The preset matching the current work and break times, or null when they are custom.</summary>
    public static SessionPreset? Match(AppSettings settings) =>
        All.FirstOrDefault(p => p.WorkMinutes == settings.WorkRequirementMinutes && p.BreakMinutes == settings.RewardDurationMinutes);

    /// <summary>Returns the settings with only the work requirement and reward duration replaced.</summary>
    public static AppSettings Apply(AppSettings settings, SessionPreset preset) =>
        Apply(settings, preset.WorkMinutes, preset.BreakMinutes);

    public static AppSettings Apply(AppSettings settings, int workMinutes, int breakMinutes)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings with { WorkRequirementMinutes = workMinutes, RewardDurationMinutes = breakMinutes };
    }
}
