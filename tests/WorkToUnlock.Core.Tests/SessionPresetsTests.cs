using WorkToUnlock.Core.Domain;

namespace WorkToUnlock.Core.Tests;

public sealed class SessionPresetsTests
{
    [Fact]
    public void Every_preset_is_a_valid_setting()
    {
        Assert.NotEmpty(SessionPresets.All);
        Assert.All(SessionPresets.All, p => Assert.Empty(SettingsValidator.Validate(SessionPresets.Apply(AppSettings.Default, p))));
    }

    [Fact]
    public void Default_settings_match_the_standard_preset()
    {
        var preset = SessionPresets.Match(AppSettings.Default);

        Assert.NotNull(preset);
        Assert.Equal((60, 20), (preset!.WorkMinutes, preset.BreakMinutes));
    }

    [Fact]
    public void Custom_times_match_no_preset()
    {
        Assert.Null(SessionPresets.Match(AppSettings.Default with { WorkRequirementMinutes = 45, RewardDurationMinutes = 15 }));
    }

    [Fact]
    public void Applying_a_preset_changes_only_work_and_break_time()
    {
        var settings = AppSettings.Default with { IdleThresholdSeconds = 120, Theme = ThemePreference.Dark, EnforcementEnabled = false };

        var applied = SessionPresets.Apply(settings, SessionPresets.All[0]);

        Assert.Equal(settings with { WorkRequirementMinutes = SessionPresets.All[0].WorkMinutes, RewardDurationMinutes = SessionPresets.All[0].BreakMinutes }, applied);
    }

    [Fact]
    public void Presets_are_distinct()
    {
        Assert.Equal(SessionPresets.All.Count, SessionPresets.All.Select(p => (p.WorkMinutes, p.BreakMinutes)).Distinct().Count());
    }
}
