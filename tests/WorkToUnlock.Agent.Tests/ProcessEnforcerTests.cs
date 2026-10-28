using Microsoft.Extensions.Logging.Abstractions;
using WorkToUnlock.Core.Domain;
using WorkToUnlock.Core.Enforcement;
using WorkToUnlock.Core.Rules;
using WorkToUnlock.Enforcement.Processes;

namespace WorkToUnlock.Agent.Tests;

public sealed class ProcessEnforcerTests : IAsyncDisposable
{
    private const string GamePath = @"D:\Games\TestGame\game.exe";
    private const string HelperPath = @"D:\Games\TestGame\bin\game-helper.exe";
    private const string EditorPath = @"C:\Tools\editor.exe";

    private static readonly ApplicationRule Game = TestRules.Restricted(1, "Test Game", GamePath, new ExecutableIdentity
    {
        FileName = "game.exe",
        InstallRoot = @"D:\Games\TestGame",
        Scope = MatchScope.InstallFolder,
        RelatedExecutables = ["game-helper.exe"],
    });

    private readonly FakeClock _clock = new();
    private readonly FakeProcessInspector _inspector = new();
    private readonly RecordingNotifier _notifier = new();
    private readonly ProcessEnforcer _enforcer;

    public ProcessEnforcerTests()
    {
        _enforcer = new ProcessEnforcer(_inspector, new ApplicationRuleMatcher(new FakeSignatureReader()), TestRules.Folders, _notifier, _clock, NullLogger<ProcessEnforcer>.Instance);
    }

    public ValueTask DisposeAsync() => _enforcer.DisposeAsync();

    private static EnforcementOptions Options(EnforcementMode mode = EnforcementMode.CloseThenTerminate) => new()
    {
        Mode = mode,
        GracefulCloseTimeout = TimeSpan.FromSeconds(15),
        ScanInterval = TimeSpan.FromHours(1), // scans are driven manually
        RecloseInterval = TimeSpan.FromSeconds(30),
    };

    private Task ApplyAsync(EnforcementMode mode = EnforcementMode.CloseThenTerminate, params ApplicationRule[] rules) =>
        _enforcer.ApplyRestrictionsAsync(rules.Length == 0 ? [Game] : rules, Options(mode), CancellationToken.None);

    [Fact]
    public async Task Running_restricted_app_is_asked_to_close_then_terminated_after_the_grace_period()
    {
        _inspector.Start(50, GamePath);

        await ApplyAsync();
        Assert.Equal([50], _inspector.CloseRequests);
        Assert.Empty(_inspector.Terminated);
        Assert.Equal(["Test Game"], _notifier.Closing);

        _clock.Advance(TimeSpan.FromSeconds(14));
        _enforcer.ScanOnce();
        Assert.Empty(_inspector.Terminated);

        _clock.Advance(TimeSpan.FromSeconds(1));
        _enforcer.ScanOnce();
        Assert.Equal([50], _inspector.Terminated);
        Assert.Equal(EnforcementHealthState.Healthy, _enforcer.Health.State);
    }

    [Fact]
    public async Task App_launched_while_restricted_is_terminated_immediately()
    {
        await ApplyAsync();

        _inspector.Start(60, GamePath);
        _inspector.Start(61, HelperPath);
        _enforcer.ScanOnce();

        Assert.Equal([60, 61], _inspector.Terminated);
        Assert.Empty(_inspector.CloseRequests);
        Assert.Contains("Test Game", _notifier.Blocked);
    }

    [Fact]
    public async Task Close_only_mode_never_terminates_and_asks_again_later()
    {
        _inspector.Start(50, GamePath);
        await ApplyAsync(EnforcementMode.CloseOnly);

        _clock.Advance(TimeSpan.FromMinutes(5));
        _enforcer.ScanOnce();

        Assert.Empty(_inspector.Terminated);
        Assert.Equal([50, 50], _inspector.CloseRequests);
    }

    [Fact]
    public async Task Unrelated_processes_are_never_touched()
    {
        _inspector.Start(70, EditorPath);
        _inspector.Start(71, @"D:\Games\Other\game.exe");
        _inspector.Start(72, @"D:\Games\TestGame\tools\unrelated.exe");

        await ApplyAsync();
        _clock.Advance(TimeSpan.FromMinutes(1));
        _enforcer.ScanOnce();

        Assert.Empty(_inspector.CloseRequests);
        Assert.Empty(_inspector.Terminated);
    }

    [Fact]
    public async Task Processes_of_other_users_other_sessions_critical_or_self_are_never_touched()
    {
        _inspector.Start(80, GamePath, owned: false, accessible: true);
        _inspector.Start(81, GamePath, session: 2);
        _inspector.Start(82, GamePath, critical: true);
        _inspector.Start(_inspector.CurrentProcessId, GamePath);

        await ApplyAsync();
        _clock.Advance(TimeSpan.FromMinutes(1));
        _enforcer.ScanOnce();

        Assert.Empty(_inspector.CloseRequests);
        Assert.Empty(_inspector.Terminated);
    }

    [Fact]
    public async Task Protected_system_executables_are_never_touched_even_with_a_bad_rule()
    {
        var bad = TestRules.Restricted(9, "Explorer", @"C:\Windows\explorer.exe");
        _inspector.Start(90, @"C:\Windows\explorer.exe");

        await ApplyAsync(EnforcementMode.CloseThenTerminate, bad);
        _clock.Advance(TimeSpan.FromMinutes(1));
        _enforcer.ScanOnce();

        Assert.Empty(_inspector.CloseRequests);
        Assert.Empty(_inspector.Terminated);
    }

    [Fact]
    public async Task Elevated_restricted_app_is_reported_as_degraded()
    {
        _inspector.Start(85, GamePath, owned: false, accessible: false);

        await ApplyAsync();

        Assert.Equal(EnforcementHealthState.Degraded, _enforcer.Health.State);
        Assert.Contains(_enforcer.Health.Problems, p => p.Contains("administrator", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Access_denied_termination_is_reported_honestly()
    {
        await ApplyAsync();
        _inspector.Start(86, GamePath);
        _inspector.DenyTermination.Add(86);

        _enforcer.ScanOnce();

        Assert.Equal(EnforcementHealthState.Degraded, _enforcer.Health.State);
    }

    [Fact]
    public async Task Multiple_instances_are_all_closed()
    {
        _inspector.Start(51, GamePath);
        _inspector.Start(52, GamePath);
        _inspector.Start(53, HelperPath);

        await ApplyAsync();
        _clock.Advance(TimeSpan.FromSeconds(15));
        _enforcer.ScanOnce();

        Assert.Equal([51, 52, 53], _inspector.Terminated.Order());
    }

    [Fact]
    public async Task Removing_restrictions_stops_enforcement()
    {
        await ApplyAsync();
        await _enforcer.RemoveRestrictionsAsync("Reward active", CancellationToken.None);

        _inspector.Start(60, GamePath);
        _enforcer.ScanOnce();

        Assert.Empty(_inspector.Terminated);
        Assert.Equal(EnforcementHealthState.Inactive, _enforcer.Health.State);
        Assert.Equal("Reward active", _enforcer.Health.Message);
    }

    [Fact]
    public async Task Updating_rules_keeps_tracking_already_handled_processes()
    {
        _inspector.Start(50, GamePath);
        await ApplyAsync();
        Assert.Single(_inspector.CloseRequests);

        await ApplyAsync(EnforcementMode.CloseThenTerminate, Game, TestRules.Restricted(2, "Other", @"D:\Other\other.exe"));

        // Still the pre-existing app: no second close request and no immediate termination.
        Assert.Single(_inspector.CloseRequests);
        Assert.Empty(_inspector.Terminated);
    }
}
