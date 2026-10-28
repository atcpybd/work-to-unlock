using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Data.Sqlite;
using WorkToUnlock.Core.Domain;
using WorkToUnlock.Core.Enforcement;
using WorkToUnlock.Core.Ipc;
using WorkToUnlock.Infrastructure.Ipc;

namespace WorkToUnlock.IntegrationTests;

/// <summary>
/// Runs the real agent executable as an isolated instance (own data folder, pipe and mutex; no startup registration,
/// no default rules) and drives it over its named pipe, using only the harmless test target as a restricted app.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AgentEndToEndTests : IDisposable
{
    private readonly string _dataDirectory = TestPaths.NewTempDirectory();
    private readonly string _instance = "itest-" + Guid.NewGuid().ToString("N")[..8];
    private readonly List<Process> _agents = [];

    private string PipeName => $"WorkToUnlock.Agent.{WindowsIdentity.GetCurrent().User!.Value}.{_instance}";

    public void Dispose()
    {
        foreach (var agent in _agents)
        {
            try
            {
                if (!agent.HasExited)
                {
                    agent.Kill();
                    agent.WaitForExit(5000);
                }
            }
            catch (InvalidOperationException)
            {
            }

            agent.Dispose();
        }

        SqliteConnection.ClearAllPools();
    }

    [WindowsFact]
    public async Task Agent_enforces_restrictions_audits_changes_and_survives_a_crash()
    {
        Assert.True(File.Exists(TestPaths.AgentExecutable), $"Build the agent first: {TestPaths.AgentExecutable}");
        var targetPath = TestPaths.CopyTestTarget();

        var agent = StartAgent();
        await using (var client = await ConnectAsync())
        {
            var status = await client.RequestAsync<AgentStatus>(AgentMethods.GetStatus, null, CancellationToken.None);
            Assert.Equal(RewardState.Working, status!.Reward.State);
            Assert.True(status.EnforcementEnabled);
            Assert.Empty((await client.RequestAsync<List<ApplicationRule>>(AgentMethods.ListRules, null, CancellationToken.None))!);

            var saved = await client.RequestAsync<SaveRuleResult>(AgentMethods.SaveRule,
                new ApplicationRule { Name = "Test target", ExecutablePath = targetPath, RuleType = RuleType.Restricted }, CancellationToken.None);
            Assert.True(saved!.Succeeded, string.Join("; ", saved.Errors.Select(e => e.Message)));
            Assert.Equal(MatchScope.InstallFolder, saved.Rule!.Identity.Scope);

            await WaitUntilAsync(client, s => s.Enforcement.RestrictionsApplied);

            using (var blocked = TargetProcess.Start(targetPath))
            {
                Assert.True(blocked.WaitForExit(TimeSpan.FromSeconds(6)), "The agent did not close the restricted app.");
            }

            var early = await client.RequestAsync<ActivationResult>(AgentMethods.ActivateReward, null, CancellationToken.None);
            Assert.Equal(ActivationOutcome.NotReady, early!.Outcome);

            var disabled = await client.RequestAsync<OperationResult>(AgentMethods.SetEnforcementEnabled, new SetEnforcementRequest(false, "integration test"), CancellationToken.None);
            Assert.True(disabled!.Succeeded);
            await WaitUntilAsync(client, s => !s.Enforcement.RestrictionsApplied);
            using (var allowed = TargetProcess.Start(targetPath))
            {
                Assert.False(allowed.WaitForExit(TimeSpan.FromSeconds(3)), "The app was closed although enforcement is disabled.");
            }

            var audit = await client.RequestAsync<AuditEventList>(AgentMethods.GetAuditEvents, new CountRequest(50), CancellationToken.None);
            Assert.Contains(audit!.Events, e => e.EventType == "EnforcementDisabled" && e.Description.Contains("integration test", StringComparison.Ordinal));
            Assert.Contains(audit.Events, e => e.EventType == "RuleAdded");
        }

        // Simulate a crash, then restart: configuration and the audited change must survive.
        agent.Kill();
        agent.WaitForExit(5000);
        agent = StartAgent();
        await using (var client = await ConnectAsync())
        {
            var status = await client.RequestAsync<AgentStatus>(AgentMethods.GetStatus, null, CancellationToken.None);
            Assert.False(status!.EnforcementEnabled);
            Assert.Single((await client.RequestAsync<List<ApplicationRule>>(AgentMethods.ListRules, null, CancellationToken.None))!);

            await client.RequestAsync<OperationResult>(AgentMethods.SetEnforcementEnabled, new SetEnforcementRequest(true, string.Empty), CancellationToken.None);
            await WaitUntilAsync(client, s => s.Enforcement.RestrictionsApplied && s.Enforcement.State == EnforcementHealthState.Healthy);
        }

        // A graceful close (as sent by Windows at sign-out or by the installer) stops the agent cleanly.
        Assert.True(NativeTestMethods.CloseWindowsOfProcess(agent.Id, "WorkToUnlock.AgentWindow") > 0);
        Assert.True(agent.WaitForExit(15_000), "The agent did not exit after WM_CLOSE.");
        Assert.Equal(0, agent.ExitCode);
    }

    [WindowsFact]
    public async Task Agent_broadcasts_status_to_subscribers_every_second()
    {
        Assert.True(File.Exists(TestPaths.AgentExecutable), $"Build the agent first: {TestPaths.AgentExecutable}");
        StartAgent();
        await using var client = await ConnectAsync();
        var received = 0;
        client.EventReceived += (_, e) =>
        {
            if (e.Name == AgentEvents.Status && e.GetPayload<AgentStatus>() is not null)
            {
                Interlocked.Increment(ref received);
            }
        };

        await client.SendAsync(AgentMethods.Subscribe, null, CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(4.5));

        // The dashboard relies on these pushes; without them it only shows the status from when it connected.
        Assert.True(received >= 3, $"Expected a status event about every second, got {received} in 4.5 s.");
    }

    private Process StartAgent()
    {
        var start = new ProcessStartInfo(TestPaths.AgentExecutable) { UseShellExecute = false };
        start.Environment["WORKTOUNLOCK_DATA_DIR"] = _dataDirectory;
        start.Environment["WORKTOUNLOCK_INSTANCE"] = _instance;
        var process = Process.Start(start) ?? throw new InvalidOperationException("Agent did not start.");
        _agents.Add(process);
        return process;
    }

    private async Task<IpcClient> ConnectAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            try
            {
                var client = await IpcClient.ConnectAsync(PipeName, TimeSpan.FromSeconds(2), requireSameUserServer: true, CancellationToken.None);
                await client.SendAsync(AgentMethods.Ping, null, CancellationToken.None);
                return client;
            }
            catch (Exception ex) when ((ex is TimeoutException or IOException) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(250);
            }
        }
    }

    private static async Task WaitUntilAsync(IpcClient client, Func<AgentStatus, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        AgentStatus? status = null;
        while (DateTime.UtcNow < deadline)
        {
            status = await client.RequestAsync<AgentStatus>(AgentMethods.GetStatus, null, CancellationToken.None);
            if (status is not null && condition(status))
            {
                return;
            }

            await Task.Delay(200);
        }

        Assert.Fail($"Condition not reached. Last enforcement state: {status?.Enforcement.State} / {status?.Enforcement.Message}");
    }
}
