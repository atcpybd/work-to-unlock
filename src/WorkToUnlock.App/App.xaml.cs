using System.Security.Principal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Serilog;
using WorkToUnlock.App.Services;
using WorkToUnlock.App.ViewModels;
using WorkToUnlock.Infrastructure;
using WorkToUnlock.Infrastructure.Logging;

namespace WorkToUnlock.App;

/// <summary>
/// The dashboard: a client of the background agent. It can be closed at any time without affecting tracking or
/// enforcement. Only one dashboard runs per user; a second launch brings the first one to the front.
/// </summary>
public partial class App : Application
{
    private Mutex? _singleInstance;
    private EventWaitHandle? _showSignal;
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            Log.Error(e.Exception, "Unhandled dashboard exception");
            e.Handled = true;
        };
    }

    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var commandLine = Environment.GetCommandLineArgs();
        var page = GetArgument(commandLine, "--page");
        var instanceKey = $"WorkToUnlock.App.{WindowsIdentity.GetCurrent().User!.Value}{AppPaths.InstanceSuffix}";

        _singleInstance = new Mutex(true, $@"Local\{instanceKey}", out var firstInstance);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{instanceKey}.Show");
        if (!firstInstance)
        {
            // Bring the running dashboard to the front (works even when the agent is down) and exit.
            _showSignal.Set();
            Exit();
            return;
        }

        var paths = AppPaths.ForCurrentUser();
        paths.EnsureCreated();
        Log.Logger = LoggingSetup.CreateLogger(paths.LogsDirectory, "dashboard");
        Log.Information("Dashboard starting");

        Services = ConfigureServices(DispatcherQueue.GetForCurrentThread());
        _window = new MainWindow(page);
        _window.Activate();
        Services.GetRequiredService<AgentConnection>().Start();

        var dispatcher = DispatcherQueue.GetForCurrentThread();
        var signal = _showSignal;
        new Thread(() =>
        {
            while (signal.WaitOne())
            {
                dispatcher.TryEnqueue(() => _window?.ShowAndNavigate(null));
            }
        })
        { IsBackground = true, Name = "Dashboard activation" }.Start();
    }

    private static ServiceProvider ConfigureServices(DispatcherQueue dispatcher)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new AgentConnection(dispatcher));
        services.AddSingleton<DialogService>();
        services.AddSingleton<PickerService>();
        services.AddSingleton<IconCache>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<ApplicationsViewModel>();
        services.AddSingleton<StatisticsViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<ProfilesViewModel>();
        return services.BuildServiceProvider();
    }

    private static string? GetArgument(string[] args, string name)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
