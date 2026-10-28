using Microsoft.Extensions.Logging.Abstractions;
using WorkToUnlock.Agent.Services;
using WorkToUnlock.Core.Persistence;

namespace WorkToUnlock.Agent.Tests;

public sealed class BlockedAttemptRecorderTests
{
    private sealed class RecordingStore : IBlockedAttemptStore
    {
        public List<(long RuleId, DateTimeOffset When)> Records { get; } = [];

        public Task RecordAsync(long ruleId, DateTimeOffset timestampUtc, CancellationToken cancellationToken)
        {
            lock (Records)
            {
                Records.Add((ruleId, timestampUtc));
            }

            return Task.CompletedTask;
        }
    }

    private readonly FakeClock _clock = new();
    private readonly RecordingStore _store = new();
    private readonly BlockedAttemptRecorder _recorder;

    public BlockedAttemptRecorderTests() => _recorder = new BlockedAttemptRecorder(_store, _clock, NullLogger<BlockedAttemptRecorder>.Instance);

    [Fact]
    public async Task Helper_processes_of_one_launch_count_as_one_attempt()
    {
        var steam = TestRules.Restricted(1, "Steam", TestRules.SteamPath);

        _recorder.OnLaunchBlocked(steam);
        _clock.Advance(TimeSpan.FromSeconds(2));
        _recorder.OnLaunchBlocked(steam); // steamwebhelper
        _recorder.OnLaunchBlocked(steam);
        await _recorder.LastWrite;

        Assert.Single(_store.Records);
    }

    [Fact]
    public async Task A_new_launch_after_the_window_counts_again()
    {
        var steam = TestRules.Restricted(1, "Steam", TestRules.SteamPath);

        _recorder.OnLaunchBlocked(steam);
        _clock.Advance(BlockedAttemptRecorder.SameLaunchWindow);
        _recorder.OnLaunchBlocked(steam);
        await _recorder.LastWrite;

        Assert.Equal(2, _store.Records.Count);
    }

    [Fact]
    public async Task Different_apps_are_counted_separately()
    {
        _recorder.OnLaunchBlocked(TestRules.Restricted(1, "Steam", TestRules.SteamPath));
        _recorder.OnLaunchBlocked(TestRules.Restricted(2, "Discord", TestRules.DiscordPath));
        await _recorder.LastWrite;

        Assert.Equal([1L, 2L], _store.Records.Select(r => r.RuleId).Order());
    }

    [Fact]
    public void Composite_notifier_forwards_to_every_listener()
    {
        var a = new RecordingNotifier();
        var b = new RecordingNotifier();
        var composite = new CompositeEnforcementNotifier([a, b]);

        composite.OnLaunchBlocked(TestRules.Restricted(1, "Steam", TestRules.SteamPath));

        Assert.Equal(["Steam"], a.Blocked);
        Assert.Equal(["Steam"], b.Blocked);
    }
}
