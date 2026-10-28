using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WorkToUnlock.App.Services;
using WorkToUnlock.App.ViewModels;
using WorkToUnlock.Core.Domain;
using WorkToUnlock.Core.Ipc;

namespace WorkToUnlock.App.Views;

public sealed partial class ApplicationsPage : Page
{
    private readonly DialogService _dialogs;

    public ApplicationsPage()
    {
        ViewModel = App.Services.GetRequiredService<ApplicationsViewModel>();
        _dialogs = App.Services.GetRequiredService<DialogService>();
        InitializeComponent();
        ViewModel.ConfirmAdd = ConfirmAddAsync;
        ViewModel.ChooseInstalledApp = ChooseInstalledAsync;
    }

    public ApplicationsViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadAsync();
    }

    private async Task<(bool Confirmed, string Name, bool CountsTowardGoal)> ConfirmAddAsync(ExecutableInspection inspection, RuleType type)
    {
        var dialog = new AddApplicationDialog(inspection, type) { XamlRoot = XamlRoot, RequestedTheme = ActualTheme };
        var result = await dialog.ShowAsync();
        return (result == ContentDialogResult.Primary, dialog.ApplicationName, dialog.CountsTowardGoal);
    }

    private async Task<string?> ChooseInstalledAsync(IReadOnlyList<InstalledApplication> apps)
    {
        if (apps.Count == 0)
        {
            await _dialogs.ShowMessageAsync("No installed apps found", "Use Browse to pick the program's .exe file instead.");
            return null;
        }

        var dialog = new InstalledAppsDialog(apps) { XamlRoot = XamlRoot, RequestedTheme = ActualTheme };
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary || dialog.ConfirmedByDoubleTap ? dialog.SelectedPath : null;
    }
}
