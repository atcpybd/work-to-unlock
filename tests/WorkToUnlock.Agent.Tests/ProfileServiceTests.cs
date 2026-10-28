using WorkToUnlock.Core.Domain;

namespace WorkToUnlock.Agent.Tests;

public sealed class ProfileServiceTests : IDisposable
{
    private readonly AgentHarness _h = new();

    public void Dispose() => _h.Dispose();

    private ApplicationRule Steam => _h.Rules.Rules.Single(r => r.Name == "Steam");

    [Fact]
    public async Task Saving_lists_the_profile_as_the_one_in_use()
    {
        await _h.StartAsync();

        var result = await _h.Profiles.SaveCurrentAsync("School", CancellationToken.None);
        var list = await _h.Profiles.GetAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        var school = Assert.Single(list.Profiles);
        Assert.Equal("School", school.Name);
        Assert.Equal(school.Id, list.ActiveProfileId);
        Assert.Contains(_h.Audit.Events, e => e.EventType == "ProfileSaved");
    }

    [Fact]
    public async Task Switching_profiles_changes_times_and_allowed_apps()
    {
        await _h.StartAsync();
        await _h.Profiles.SaveCurrentAsync("School", CancellationToken.None);

        await _h.Settings.UpdateAsync(_h.Settings.Current with { WorkRequirementMinutes = 25, RewardDurationMinutes = 15 }, CancellationToken.None);
        await _h.Rules.SaveAsync(Steam with { Enabled = false }, CancellationToken.None);
        await _h.Profiles.SaveCurrentAsync("Weekend", CancellationToken.None);

        var school = (await _h.Profiles.GetAsync(CancellationToken.None)).Profiles.Single(p => p.Name == "School");
        var result = await _h.Profiles.ApplyAsync(school.Id, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(60, _h.Settings.Current.WorkRequirementMinutes);
        Assert.Equal(20, _h.Settings.Current.RewardDurationMinutes);
        Assert.True(Steam.Enabled);
        Assert.Equal(school.Id, (await _h.Profiles.GetAsync(CancellationToken.None)).ActiveProfileId);
        Assert.Contains(_h.Audit.Events, e => e.EventType == "ProfileApplied" && e.Description.Contains("School", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Saving_with_an_existing_name_replaces_that_profile()
    {
        await _h.StartAsync();
        await _h.Profiles.SaveCurrentAsync("School", CancellationToken.None);
        var id = (await _h.Profiles.GetAsync(CancellationToken.None)).Profiles[0].Id;

        await _h.Settings.UpdateAsync(_h.Settings.Current with { RewardDurationMinutes = 10 }, CancellationToken.None);
        await _h.Profiles.SaveCurrentAsync("school", CancellationToken.None);

        var profile = Assert.Single((await _h.Profiles.GetAsync(CancellationToken.None)).Profiles);
        Assert.Equal(id, profile.Id);
        Assert.Equal(10, profile.RewardDurationMinutes);
    }

    [Fact]
    public async Task Deleted_profiles_are_gone_and_unknown_ones_are_reported()
    {
        await _h.StartAsync();
        await _h.Profiles.SaveCurrentAsync("School", CancellationToken.None);
        var id = (await _h.Profiles.GetAsync(CancellationToken.None)).Profiles[0].Id;

        Assert.True((await _h.Profiles.DeleteAsync(id, CancellationToken.None)).Succeeded);
        Assert.Empty((await _h.Profiles.GetAsync(CancellationToken.None)).Profiles);
        Assert.False((await _h.Profiles.ApplyAsync(id, CancellationToken.None)).Succeeded);
    }

    [Fact]
    public async Task An_empty_name_is_rejected()
    {
        await _h.StartAsync();

        var result = await _h.Profiles.SaveCurrentAsync("  ", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Empty((await _h.Profiles.GetAsync(CancellationToken.None)).Profiles);
    }
}
