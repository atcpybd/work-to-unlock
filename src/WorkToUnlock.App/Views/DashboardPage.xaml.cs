using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WorkToUnlock.App.Services;
using WorkToUnlock.App.ViewModels;
using WorkToUnlock.Core.Domain;

namespace WorkToUnlock.App.Views;

public sealed partial class DashboardPage : Page
{
    private readonly DialogService _dialogs;

    public DashboardPage()
    {
        ViewModel = App.Services.GetRequiredService<DashboardViewModel>();
        _dialogs = App.Services.GetRequiredService<DialogService>();
        InitializeComponent();
        ViewModel.AskCustomTimes = AskCustomTimesAsync;
    }

    public DashboardViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadRulesAsync();
        await ViewModel.LoadSettingsAsync();
    }

    private async Task<(int WorkMinutes, int BreakMinutes)?> AskCustomTimesAsync(int workMinutes, int breakMinutes)
    {
        var work = new NumberBox
        {
            Header = "Work time (minutes)",
            Value = workMinutes,
            Minimum = SettingsValidator.MinWorkMinutes,
            Maximum = SettingsValidator.MaxWorkMinutes,
            SmallChange = 5,
            LargeChange = 15,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten,
        };
        var rest = new NumberBox
        {
            Header = "Break time (minutes)",
            Value = breakMinutes,
            Minimum = SettingsValidator.MinRewardMinutes,
            Maximum = SettingsValidator.MaxRewardMinutes,
            SmallChange = 5,
            LargeChange = 15,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten,
        };

        var content = new StackPanel { Spacing = 12, MinWidth = 320 };
        content.Children.Add(new TextBlock
        {
            Text = $"Work {SettingsValidator.MinWorkMinutes}–{SettingsValidator.MaxWorkMinutes} minutes to earn a break of {SettingsValidator.MinRewardMinutes}–{SettingsValidator.MaxRewardMinutes} minutes.",
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["SecondaryTextStyle"],
        });
        content.Children.Add(work);
        content.Children.Add(rest);

        var dialog = _dialogs.Create("Custom work and break", "Save", "Cancel");
        dialog.Content = content;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || double.IsNaN(work.Value) || double.IsNaN(rest.Value))
        {
            return null;
        }

        return ((int)Math.Round(work.Value), (int)Math.Round(rest.Value));
    }
}
