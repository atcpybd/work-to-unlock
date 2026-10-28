using System.Security.Principal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using WorkToUnlock.Core.Ipc;
using WorkToUnlock.Infrastructure;
using WorkToUnlock.Infrastructure.Ipc;
using WorkToUnlock.Infrastructure.Logging;
using WorkToUnlock.Infrastructure.Persistence;

namespace WorkToUnlock.Agent;

/// <summary>
/// Per-user background agent. Starts at sign-in, runs without administrator rights, and keeps tracking and
/// enforcing whether or not the dashboard is open.
/// </summary>
public static class Program
{
    public const string ShowDashboardArgument = "--show-dashboard";

    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("The Work to Unlock agent runs on Windows only.");
            return 1;
        }

        var paths = AppPaths.ForCurrentUser();
        paths.EnsureCreated();
        Log.Logger = LoggingSetup.CreateLogger(paths.LogsDirectory, "agent", args.Contains("--verbose"));
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception; Windows will restart the agent");
            Log.CloseAndFlush();
        };

        try
        {
            using var mutex = new Mutex(initiallyOwned: true, $@"Local\WorkToUnlock.Agent.{WindowsIdentity.GetCurrent().User!.Value}{AppPaths.InstanceSuffix}", out var firstInstance);
            if (!firstInstance)
            {
                Log.Information("Another agent instance is already running");
                if (args.Contains(ShowDashboardArgument))
                {
                    await AskRunningAgentToShowDashboardAsync().ConfigureAwait(false);
                }

                return 0;
            }

            if (args.Contains("--autostart") && !await IsAutostartEnabledAsync(paths).ConfigureAwait(false))
            {
                Log.Information("'Start with Windows' is turned off; exiting");
                return 0;
            }

            Log.Information("Work to Unlock agent starting ({Args})", string.Join(' ', args));
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                Args = args,
                ContentRootPath = AppContext.BaseDirectory,
                DisableDefaults = true,
            });
            builder.Logging.ClearProviders();
            builder.Services.AddSerilog();
            builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(10));
            AgentComposition.Register(builder.Services, paths);

            using var host = builder.Build();
            await host.RunAsync().ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "The agent terminated unexpectedly");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Reads the "Start with Windows" setting before the host starts. Errors default to starting (fail-safe).</summary>
    private static async Task<bool> IsAutostartEnabledAsync(AppPaths paths)
    {
        if (!File.Exists(paths.DatabasePath))
        {
            return true;
        }

        try
        {
            var database = new SqliteDatabase(paths.DatabasePath, paths.BackupPath, NullLogger<SqliteDatabase>.Instance);
            var store = new SqliteSettingsStore(database, NullLogger<SqliteSettingsStore>.Instance);
            return (await store.LoadAsync(CancellationToken.None).ConfigureAwait(false)).StartWithWindows;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not read the startup setting; starting anyway");
            return true;
        }
    }

    private static async Task AskRunningAgentToShowDashboardAsync()
    {
        try
        {
            await using var client = await IpcClient.ConnectAsync(PipeSecurityFactory.AgentPipeNameForCurrentUser(), TimeSpan.FromSeconds(3), requireSameUserServer: true, CancellationToken.None).ConfigureAwait(false);
            await client.SendAsync(AgentMethods.ShowDashboard, new ShowDashboardRequest(null), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or IpcException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Could not reach the running agent");
        }
    }
}
