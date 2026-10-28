using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WorkToUnlock.Core.Domain;
using WorkToUnlock.Core.Enforcement;
using WorkToUnlock.Core.Ipc;
using WorkToUnlock.Core.Rules;
using WorkToUnlock.Core.Time;
using WorkToUnlock.Enforcement.Processes;
using WorkToUnlock.Infrastructure.Ipc;
using WorkToUnlock.Infrastructure.Windows;

namespace WorkToUnlock.IntegrationTests;

[SupportedOSPlatform("windows")]
public sealed class Win32MonitorTests
{
    [WindowsFact]
    public void Foreground_monitor_returns_a_full_path_or_nothing()
    {
        var app = new Win32ForegroundAppMonitor().GetForegroundApp();

        // Non-interactive sessions (CI services) have no foreground window; that must not throw.
        if (app is not null)
        {
            Assert.True(WindowsPath.IsFullyQualified(app.ExecutablePath), app.ExecutablePath);
            Assert.True(app.ProcessId > 0);
        }
    }

    [WindowsFact]
    public void Idle_detector_reports_a_plausible_duration()
    {
        var idle = new Win32IdleDetector(NullLogger<Win32IdleDetector>.Instance).GetIdleDuration();

        Assert.True(idle >= TimeSpan.Zero);
        Assert.True(idle < TimeSpan.FromDays(50), idle.ToString());
    }

    [WindowsFact]
    public void Boot_id_is_available()
    {
        Assert.False(string.IsNullOrEmpty(WindowsBootId.Read()));
    }
}

[SupportedOSPlatform("windows")]
public sealed class SignatureTests
{
    private readonly AuthenticodeSignatureReader _reader = new(NullLogger<AuthenticodeSignatureReader>.Instance);

    [WindowsFact]
    public void Microsoft_signed_executable_reports_its_publisher()
    {
        var runtime = RuntimeEnvironment.GetRuntimeDirectory();
        var dotnet = Path.GetFullPath(Path.Combine(runtime, "..", "..", "..", "dotnet.exe"));
        Assert.True(File.Exists(dotnet), dotnet);

        var signature = _reader.GetSignature(dotnet);

        // The publisher is the signer certificate's simple display name (its CN): ".NET" for dotnet.exe.
        Assert.NotNull(signature);
        Assert.True(signature!.Trusted);
        Assert.Equal(".NET", signature.Publisher);
    }

    [WindowsFact]
    public void Unsigned_executable_has_no_trusted_publisher()
    {
        var signature = _reader.GetSignature(TestPaths.CopyTestTarget());

        Assert.NotNull(signature);
        Assert.False(signature!.Trusted);
        Assert.Null(signature.Publisher);
    }

    [WindowsFact]
    public void Missing_file_returns_null()
    {
        Assert.Null(_reader.GetSignature(@"C:\does\not\exist.exe"));
    }
}

[SupportedOSPlatform("windows")]
public sealed class ProcessInspectorTests
{
    private readonly Win32ProcessInspector _inspector = new(NullLogger<Win32ProcessInspector>.Instance);

    private ProcessEntry Find(TargetProcess target) =>
        Assert.Single(_inspector.Snapshot(), p => p.ProcessId == target.Id);

    [WindowsFact]
    public void Snapshot_identifies_the_target_and_its_owner()
    {
        var path = TestPaths.CopyTestTarget();
        using var target = TargetProcess.Start(path);

        var entry = Find(target);

        Assert.Equal(path, entry.ExecutablePath, ignoreCase: true);
        Assert.True(entry.OwnedByCurrentUser);
        Assert.True(entry.Accessible);
        Assert.False(entry.IsCritical);
        Assert.Equal(_inspector.CurrentSessionId, entry.SessionId);
    }

    [WindowsFact]
    public void Graceful_close_closes_a_cooperative_app()
    {
        using var target = TargetProcess.Start(TestPaths.CopyTestTarget());

        Assert.Equal(1, _inspector.RequestClose(Find(target)));

        Assert.True(target.WaitForExit(TimeSpan.FromSeconds(10)));
    }

    [WindowsFact]
    public void Termination_verifies_identity_before_acting()
    {
        using var target = TargetProcess.Start(TestPaths.CopyTestTarget());
        var entry = Find(target);

        Assert.Equal(TerminationOutcome.IdentityChanged, _inspector.Terminate(entry with { CreationTime = entry.CreationTime + 1 }));
        Assert.Equal(TerminationOutcome.IdentityChanged, _inspector.Terminate(entry with { ExecutablePath = @"C:\Other\app.exe" }));
        Assert.False(target.Process.HasExited);

        Assert.Equal(TerminationOutcome.Terminated, _inspector.Terminate(entry));
        Assert.True(target.WaitForExit(TimeSpan.FromSeconds(5)));
    }

    [WindowsFact]
    public void Apps_that_ignore_close_requests_keep_running_until_terminated()
    {
        using var target = TargetProcess.Start(TestPaths.CopyTestTarget(), ignoreClose: true);

        _inspector.RequestClose(Find(target));

        Assert.False(target.WaitForExit(TimeSpan.FromSeconds(2)));
        Assert.Equal(TerminationOutcome.Terminated, _inspector.Terminate(Find(target)));
    }
}

[SupportedOSPlatform("windows")]
public sealed class ProcessEnforcerIntegrationTests
{
    private sealed class NullNotifier : IEnforcementNotifier
    {
        public void OnLaunchBlocked(ApplicationRule rule)
        {
        }

        public void OnClosingRestrictedApp(ApplicationRule rule, TimeSpan gracePeriod)
        {
        }
    }

    private static ProcessEnforcer CreateEnforcer() => new(
        new Win32ProcessInspector(NullLogger<Win32ProcessInspector>.Instance),
        new ApplicationRuleMatcher(new AuthenticodeSignatureReader(NullLogger<AuthenticodeSignatureReader>.Instance)),
        WindowsSystemFolders.Create(AppContext.BaseDirectory),
        new NullNotifier(),
        new SystemClock(null),
        NullLogger<ProcessEnforcer>.Instance);

    private static EnforcementOptions Options(int graceSeconds) => new()
    {
        Mode = EnforcementMode.CloseThenTerminate,
        GracefulCloseTimeout = TimeSpan.FromSeconds(graceSeconds),
        ScanInterval = TimeSpan.FromMilliseconds(250),
    };

    [WindowsFact]
    public async Task App_launched_while_restricted_is_closed_within_seconds()
    {
        var path = TestPaths.CopyTestTarget();
        await using var enforcer = CreateEnforcer();
        await enforcer.ApplyRestrictionsAsync([TestRules.Restricted(1, "Target", path)], Options(30), CancellationToken.None);

        using var target = TargetProcess.Start(path);

        Assert.True(target.WaitForExit(TimeSpan.FromSeconds(5)), "The restricted test app was not closed.");
        Assert.Equal(EnforcementHealthState.Healthy, enforcer.Health.State);
    }

    [WindowsFact]
    public async Task Running_app_gets_its_grace_period_before_termination()
    {
        var path = TestPaths.CopyTestTarget();
        using var target = TargetProcess.Start(path, ignoreClose: true);
        await using var enforcer = CreateEnforcer();

        await enforcer.ApplyRestrictionsAsync([TestRules.Restricted(1, "Target", path)], Options(3), CancellationToken.None);

        Assert.False(target.WaitForExit(TimeSpan.FromSeconds(1.5)), "Terminated before the grace period ended.");
        Assert.True(target.WaitForExit(TimeSpan.FromSeconds(6)), "Not terminated after the grace period.");
    }

    [WindowsFact]
    public async Task Copies_in_other_folders_and_lifted_restrictions_are_left_alone()
    {
        var restrictedPath = TestPaths.CopyTestTarget();
        var otherPath = TestPaths.CopyTestTarget();
        await using var enforcer = CreateEnforcer();
        await enforcer.ApplyRestrictionsAsync([TestRules.Restricted(1, "Target", restrictedPath)], Options(30), CancellationToken.None);

        using var other = TargetProcess.Start(otherPath);
        Assert.False(other.WaitForExit(TimeSpan.FromSeconds(2)));

        await enforcer.RemoveRestrictionsAsync("Reward active", CancellationToken.None);
        using var allowed = TargetProcess.Start(restrictedPath);
        Assert.False(allowed.WaitForExit(TimeSpan.FromSeconds(2)));
    }
}

[SupportedOSPlatform("windows")]
public sealed class PipeSecurityTests
{
    private sealed class PingHandler : IIpcRequestHandler
    {
        public Task<object?> HandleAsync(IpcClientContext client, string method, JsonElement? payload, CancellationToken cancellationToken) =>
            Task.FromResult<object?>(new { Method = method, Pid = client.ProcessId, Path = client.ExecutablePath });
    }

    [WindowsFact]
    public async Task Agent_pipe_serves_the_current_user_and_denies_the_network()
    {
        var name = "WorkToUnlock.Test." + Guid.NewGuid().ToString("N");
        await using var server = new PipeServer(
            new PipeServerOptions { PipeName = name, SecurityFactory = PipeSecurityFactory.CurrentUserOnly },
            new PingHandler(),
            NullLogger<PipeServer>.Instance);
        server.Start();

        await using var client = await IpcClient.ConnectAsync(name, TimeSpan.FromSeconds(5), requireSameUserServer: true, CancellationToken.None);
        var response = await client.SendAsync(AgentMethods.Ping, null, CancellationToken.None);

        Assert.Equal(Environment.ProcessId, response!.Value.GetProperty("pid").GetInt32());

        var security = PipeSecurityFactory.CurrentUserOnly();
        var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();
        Assert.Contains(rules, r => r.AccessControlType == AccessControlType.Deny && r.IdentityReference.Value == new SecurityIdentifier(WellKnownSidType.NetworkSid, null).Value);
        Assert.All(rules.Where(r => r.AccessControlType == AccessControlType.Allow), r => Assert.Equal(WindowsIdentity.GetCurrent().User!.Value, r.IdentityReference.Value));
    }
}
