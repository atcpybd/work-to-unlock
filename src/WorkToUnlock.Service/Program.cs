using System.ServiceProcess;
using System.Text.Json;
using System.Security.Principal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using WorkToUnlock.Enforcement.AppLocker;
using WorkToUnlock.Infrastructure.Logging;

namespace WorkToUnlock.Service;

/// <summary>
/// Strict-mode service host and administrator command line:
///   WorkToUnlock.Service.exe                 run as the Windows service
///   WorkToUnlock.Service.exe consent --enable | --disable
///   WorkToUnlock.Service.exe rollback        remove every Work to Unlock AppLocker rule (uninstall/recovery)
///   WorkToUnlock.Service.exe status
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("The Work to Unlock service runs on Windows only.");
            return 1;
        }

        try
        {
            StrictModeState.EnsureDirectory();
        }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Administrator rights are required.");
            return 5;
        }

        Log.Logger = LoggingSetup.CreateLogger(StrictModeState.LogsDirectory, "service");
        try
        {
            return args.Length > 0 ? await RunCommandAsync(args).ConfigureAwait(false) : await RunServiceAsync(args).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "The service failed");
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync().ConfigureAwait(false);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task<int> RunServiceAsync(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddWindowsService(o => o.ServiceName = StrictModeState.ServiceName);
        builder.Logging.ClearProviders();
        builder.Services.AddSerilog();
        builder.Services.AddSingleton(sp => new AppLockerPolicyStore(StrictModeState.Directory, sp.GetRequiredService<ILogger<AppLockerPolicyStore>>()));
        builder.Services.AddSingleton<StrictEnforcementManager>();
        builder.Services.AddSingleton<StrictRequestHandler>();
        builder.Services.AddHostedService<StrictEnforcementService>();
        await builder.Build().RunAsync().ConfigureAwait(false);
        return 0;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task<int> RunCommandAsync(string[] args)
    {
        if (!StrictModeState.IsAdministrator())
        {
            Console.Error.WriteLine("Run this command from an elevated (administrator) prompt.");
            return 5;
        }

        using var loggerFactory = LoggerFactory.Create(b => b.AddSerilog());
        var store = new AppLockerPolicyStore(StrictModeState.Directory, loggerFactory.CreateLogger<AppLockerPolicyStore>());
        var manager = new StrictEnforcementManager(store, loggerFactory.CreateLogger<StrictEnforcementManager>());
        var who = WindowsIdentity.GetCurrent().Name;

        switch (args[0].ToLowerInvariant())
        {
            case "consent" when args.Contains("--enable"):
            {
                var support = AppLockerCompatibility.Check();
                if (!support.Supported)
                {
                    Console.Error.WriteLine(support.Message);
                    Log.Warning("Strict mode not enabled: {Message}", support.Message);
                    return 2;
                }

                StrictModeState.SaveConsent(new StrictModeConsent { Enabled = true, ChangedBy = who, ChangedUtc = DateTimeOffset.UtcNow });
                Log.Information("Strict mode enabled by {User}", who);
                StartService();
                Console.WriteLine("Strict mode enabled. Choose 'Strict' as the enforcement mode in Work to Unlock.");
                return 0;
            }

            case "consent" when args.Contains("--disable"):
            {
                StrictModeState.SaveConsent(new StrictModeConsent { Enabled = false, ChangedBy = who, ChangedUtc = DateTimeOffset.UtcNow });
                var result = await manager.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                Log.Information("Strict mode disabled by {User}: {Message}", who, result.Message);
                Console.WriteLine(result.Message);
                return result.Succeeded ? 0 : 3;
            }

            case "rollback":
            {
                var ownership = store.LoadOwnership();
                if (ownership.IsEmpty && !File.Exists(StrictModeState.ConsentPath))
                {
                    Console.WriteLine("Nothing to roll back.");
                    return 0;
                }

                var result = await manager.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                StrictModeState.SaveConsent(new StrictModeConsent { Enabled = false, ChangedBy = who, ChangedUtc = DateTimeOffset.UtcNow });
                Console.WriteLine(result.Message);
                return result.Succeeded ? 0 : 3;
            }

            case "status":
                Console.WriteLine(JsonSerializer.Serialize(manager.GetStatus(null), new JsonSerializerOptions { WriteIndented = true }));
                return 0;

            default:
                Console.Error.WriteLine("Usage: WorkToUnlock.Service.exe [consent --enable|--disable | rollback | status]");
                return 64;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void StartService()
    {
        try
        {
            using var service = new ServiceController(StrictModeState.ServiceName);
            if (service.Status == ServiceControllerStatus.Stopped)
            {
                service.Start();
            }
        }
        catch (InvalidOperationException)
        {
            Console.WriteLine("The strict enforcement service is not installed; install the 'Strict enforcement' feature to use strict mode.");
        }
    }
}
