using WorkToUnlock.Agent.Services;
using WorkToUnlock.Core.Domain;

namespace WorkToUnlock.Agent.Tests;

public sealed class UpdateServiceTests : IDisposable
{
    private const string Download = "https://github.com/atcpybd/work-to-unlock/releases/download/v99.0.0/";
    private static readonly string NewRelease = $$"""
        {
          "tag_name": "v99.0.0",
          "html_url": "https://github.com/atcpybd/work-to-unlock/releases/tag/v99.0.0",
          "assets": [
            { "name": "WorkToUnlock-99.0.0-x64.msi", "size": 4096, "browser_download_url": "{{Download}}WorkToUnlock-99.0.0-x64.msi" },
            { "name": "SHA256SUMS.txt", "size": 200, "browser_download_url": "{{Download}}SHA256SUMS.txt" }
          ]
        }
        """;

    private readonly AgentHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task The_first_check_waits_a_little_after_start()
    {
        await _h.StartAsync();
        _h.Releases.Json = NewRelease;

        await _h.Updates.CheckIfDueAsync(CancellationToken.None);
        Assert.Equal(0, _h.Releases.Calls);

        _h.Clock.Advance(UpdateService.FirstCheckDelay);
        await _h.Updates.CheckIfDueAsync(CancellationToken.None);
        Assert.Equal(1, _h.Releases.Calls);
    }

    [Fact]
    public async Task A_new_version_is_announced_once_and_checked_daily()
    {
        await _h.StartAsync();
        _h.Releases.Json = NewRelease;
        _h.Clock.Advance(UpdateService.FirstCheckDelay);

        await _h.Updates.CheckIfDueAsync(CancellationToken.None);
        _h.Clock.Advance(TimeSpan.FromHours(1));
        await _h.Updates.CheckIfDueAsync(CancellationToken.None);
        _h.Clock.Advance(UpdateService.CheckInterval);
        await _h.Updates.CheckIfDueAsync(CancellationToken.None);

        Assert.Equal(2, _h.Releases.Calls);
        Assert.Single(_h.Tray.Notifications, n => n.Title == "Work to Unlock 99.0.0 is available");
        Assert.True((await _h.Updates.GetAsync(false, CancellationToken.None)).IsUpdateAvailable);
    }

    [Fact]
    public async Task Nothing_is_checked_automatically_when_turned_off_but_checking_now_still_works()
    {
        _h.SettingsStore.Settings = AppSettings.Default with { CheckForUpdates = false };
        await _h.StartAsync();
        _h.Releases.Json = NewRelease;
        _h.Clock.Advance(TimeSpan.FromDays(2));

        await _h.Updates.CheckIfDueAsync(CancellationToken.None);
        Assert.Equal(0, _h.Releases.Calls);

        var info = await _h.Updates.GetAsync(true, CancellationToken.None);
        Assert.True(info.IsUpdateAvailable);
        Assert.Empty(_h.Tray.Notifications);
    }

    [Fact]
    public async Task A_failed_check_is_reported_and_retried_sooner()
    {
        await _h.StartAsync();
        _h.Releases.Failure = new HttpRequestException("offline");
        _h.Clock.Advance(UpdateService.FirstCheckDelay);

        await _h.Updates.CheckIfDueAsync(CancellationToken.None);
        Assert.NotNull((await _h.Updates.GetAsync(false, CancellationToken.None)).Error);

        _h.Releases.Failure = null;
        _h.Releases.Json = NewRelease;
        _h.Clock.Advance(UpdateService.RetryInterval);
        await _h.Updates.CheckIfDueAsync(CancellationToken.None);

        var info = await _h.Updates.GetAsync(false, CancellationToken.None);
        Assert.Null(info.Error);
        Assert.True(info.IsUpdateAvailable);
    }

    [Fact]
    public async Task No_published_release_is_not_an_error()
    {
        await _h.StartAsync();

        var info = await _h.Updates.GetAsync(true, CancellationToken.None);

        Assert.False(info.IsUpdateAvailable);
        Assert.Null(info.Error);
    }
}
