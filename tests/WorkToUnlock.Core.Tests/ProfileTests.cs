using WorkToUnlock.Core.Domain;

namespace WorkToUnlock.Core.Tests;

public sealed class ProfileTests
{
    private static readonly ApplicationRule Code = TestRules.Work(1, "Code", TestRules.VsCodePath);
    private static readonly ApplicationRule Steam = TestRules.Restricted(2, "Steam", TestRules.SteamPath);
    private static readonly ApplicationRule Discord = TestRules.Restricted(3, "Discord", TestRules.DiscordPath);

    private static readonly AppSettings School = AppSettings.Default with { WorkRequirementMinutes = 60, RewardDurationMinutes = 10, DailyGoalMinutes = 180 };

    [Fact]
    public void Capturing_records_times_and_restricted_apps_that_are_turned_off()
    {
        var profile = Profiles.Capture("w", " Weekend ", School with { RewardMode = RewardMode.Balance }, [Code, Steam, Discord with { Enabled = false }]);

        Assert.Equal("Weekend", profile.Name);
        Assert.Equal(60, profile.WorkRequirementMinutes);
        Assert.Equal(10, profile.RewardDurationMinutes);
        Assert.Equal(RewardMode.Balance, profile.RewardMode);
        Assert.Equal(180, profile.DailyGoalMinutes);
        Assert.Equal([3L], profile.AllowedAppIds);
    }

    [Fact]
    public void Applying_changes_times_and_which_apps_are_blocked()
    {
        var weekend = new Profile { Id = "w", Name = "Weekend", WorkRequirementMinutes = 25, RewardDurationMinutes = 15, BalanceCapMinutes = 60, AllowedAppIds = [3] };

        var settings = Profiles.ApplyTo(School, weekend);
        var changes = Profiles.RuleChanges(weekend, [Code, Steam, Discord]);

        Assert.Equal(25, settings.WorkRequirementMinutes);
        Assert.Equal(15, settings.RewardDurationMinutes);
        Assert.Equal(School.EnforcementMode, settings.EnforcementMode);
        var discord = Assert.Single(changes);
        Assert.Equal(3, discord.Id);
        Assert.False(discord.Enabled);
    }

    [Fact]
    public void Apps_added_after_saving_stay_blocked()
    {
        var school = Profiles.Capture("s", "School", School, [Steam]);
        var newGame = TestRules.Restricted(9, "New game", @"C:\Games\new.exe") with { Enabled = false };

        var changes = Profiles.RuleChanges(school, [Steam, newGame]);

        Assert.True(Assert.Single(changes).Enabled);
    }

    [Fact]
    public void A_profile_matches_the_setup_it_was_captured_from()
    {
        ApplicationRule[] rules = [Code, Steam, Discord with { Enabled = false }];
        var profile = Profiles.Capture("w", "Weekend", School, rules);

        Assert.True(Profiles.Matches(profile, School, rules));
        Assert.False(Profiles.Matches(profile, School with { RewardDurationMinutes = 11 }, rules));
        Assert.False(Profiles.Matches(profile, School, [Code, Steam, Discord]));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("School", true)]
    public void Names_must_not_be_empty(string name, bool valid)
    {
        Assert.Equal(valid, Profiles.ValidateName(name, []).Count == 0);
    }

    [Fact]
    public void The_number_of_profiles_is_limited_but_existing_ones_can_be_replaced()
    {
        var full = Enumerable.Range(0, Profiles.MaxProfiles).Select(i => new Profile { Id = $"{i}", Name = $"P{i}" }).ToList();

        Assert.NotEmpty(Profiles.ValidateName("Another", full));
        Assert.Empty(Profiles.ValidateName("p3", full));
        Assert.NotEmpty(Profiles.ValidateName(new string('x', Profiles.MaxNameLength + 1), []));
    }
}
