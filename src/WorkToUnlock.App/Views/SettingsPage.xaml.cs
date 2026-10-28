using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WorkToUnlock.App.Services;
using WorkToUnlock.App.ViewModels;

namespace WorkToUnlock.App.Views;

public sealed partial class SettingsPage : Page
{
    private readonly DialogService _dialogs;

    public SettingsPage()
    {
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
        _dialogs = App.Services.GetRequiredService<DialogService>();
        InitializeComponent();
        ViewModel.AskDisableReason = AskDisableReasonAsync;
    }

    public SettingsViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadAsync();
    }

    private async void OnEnforcementToggled(object sender, RoutedEventArgs e) =>
        await ViewModel.SetEnforcementEnabledAsync(EnforcementToggle.IsOn);

    private async Task<string?> AskDisableReasonAsync()
    {
        var reason = new TextBox
        {
            Header = "Why are you turning off enforcement?",
            PlaceholderText = "For example: exam prep is over, travelling, …",
            AcceptsReturn = false,
        };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = "Restricted apps will be able to run at any time until you turn enforcement back on. This change is recorded in the activity log.",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(reason);

        var dialog = _dialogs.Create("Turn off enforcement?", "Turn off", "Keep enforcing");
        dialog.Content = content;
        dialog.DefaultButton = ContentDialogButton.Close;
        dialog.IsPrimaryButtonEnabled = false;
        reason.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(reason.Text);

        return await dialog.ShowAsync() == ContentDialogResult.Primary ? reason.Text.Trim() : null;
    }
}
