using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;
using WorkToUnlock.App.Services;
using WorkToUnlock.App.ViewModels;
using WorkToUnlock.App.Views;
using WorkToUnlock.Core.Domain;

namespace WorkToUnlock.App;

public sealed partial class MainWindow : Window
{
    private static readonly Dictionary<string, Type> Pages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dashboard"] = typeof(DashboardPage),
        ["applications"] = typeof(ApplicationsPage),
        ["statistics"] = typeof(StatisticsPage),
        ["settings"] = typeof(SettingsPage),
    };

    private readonly string _initialPage;
    private bool _exiting;

    public MainWindow(string? initialPage)
    {
        Shell = App.Services.GetRequiredService<ShellViewModel>();
        _initialPage = initialPage is not null && Pages.ContainsKey(initialPage) ? initialPage : "dashboard";
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SystemBackdrop = new MicaBackdrop();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        AppWindow.Title = "Work to Unlock";

        var scale = GetScale();
        AppWindow.Resize(new SizeInt32((int)(1180 * scale), (int)(820 * scale)));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(760 * scale);
            presenter.PreferredMinimumHeight = (int)(560 * scale);
        }

        AppWindow.Closing += OnClosing;
        App.Services.GetRequiredService<PickerService>().WindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);

        var agent = App.Services.GetRequiredService<AgentConnection>();
        agent.ShowRequested += (_, page) => ShowAndNavigate(page);
        agent.ExitRequested += (_, _) => ExitApplication();
        Shell.PropertyChanged += OnShellPropertyChanged;
    }

    public ShellViewModel Shell { get; }

    public void ShowAndNavigate(string? page)
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }

        Activate();
        if (page is not null)
        {
            Navigate(page);
        }
    }

    public void ExitApplication()
    {
        _exiting = true;
        Application.Current.Exit();
    }

    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        App.Services.GetRequiredService<DialogService>().XamlRoot = RootGrid.XamlRoot;
        ApplyTheme(Shell.Settings.Theme);
        Navigate(_initialPage);
    }

    private void Navigate(string page)
    {
        if (!Pages.TryGetValue(page, out var type))
        {
            return;
        }

        var item = Navigation.MenuItems.Concat(Navigation.FooterMenuItems).OfType<NavigationViewItem>()
            .FirstOrDefault(i => string.Equals(i.Tag as string, page, StringComparison.OrdinalIgnoreCase));
        if (item is not null && !ReferenceEquals(Navigation.SelectedItem, item))
        {
            Navigation.SelectedItem = item; // raises SelectionChanged, which navigates
            return;
        }

        if (ContentFrame.CurrentSourcePageType != type)
        {
            ContentFrame.Navigate(type, null, new EntranceNavigationTransitionInfo());
        }
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string tag } && Pages.TryGetValue(tag, out var type) && ContentFrame.CurrentSourcePageType != type)
        {
            ContentFrame.Navigate(type, null, new EntranceNavigationTransitionInfo());
        }
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        // Closing the dashboard never stops the agent. With "minimize to tray" the window just hides so the tray can reopen it quickly.
        if (!_exiting && Shell.Settings.MinimizeToTray)
        {
            args.Cancel = true;
            sender.Hide();
        }
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.Settings))
        {
            ApplyTheme(Shell.Settings.Theme);
        }
    }

    private void ApplyTheme(ThemePreference theme)
    {
        if (Content is not FrameworkElement root)
        {
            return;
        }

        root.RequestedTheme = theme switch
        {
            ThemePreference.Light => ElementTheme.Light,
            ThemePreference.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        var dark = root.ActualTheme == ElementTheme.Dark;
        var titleBar = AppWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        titleBar.ButtonHoverBackgroundColor = dark ? ColorHelper.FromArgb(0x20, 0xFF, 0xFF, 0xFF) : ColorHelper.FromArgb(0x10, 0, 0, 0);
        titleBar.ButtonHoverForegroundColor = titleBar.ButtonForegroundColor;
    }

    private double GetScale()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpi = NativeDpi.GetDpiForWindow(hwnd);
        return dpi == 0 ? 1 : dpi / 96d;
    }
}

internal static partial class NativeDpi
{
    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    internal static partial uint GetDpiForWindow(nint hwnd);
}
