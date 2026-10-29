using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WorkToUnlock.App.Controls;
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
        Profiles = App.Services.GetRequiredService<ProfilesViewModel>();
        Updates = App.Services.GetRequiredService<UpdatesViewModel>();
        _dialogs = App.Services.GetRequiredService<DialogService>();
        InitializeComponent();
        ViewModel.AskCustomTimes = AskCustomTimesAsync;
    }

    public DashboardViewModel ViewModel { get; }

    public ProfilesViewModel Profiles { get; }

    public UpdatesViewModel Updates { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.RewardStateChanged += OnRewardStateChanged;
        ViewModel.DailyGoalReached += OnDailyGoalReached;
        _ = Profiles.LoadAsync();
        _ = Updates.LoadAsync();

        // Daily totals come from saved sessions, so a slow refresh is enough.
        _progressTimer ??= DispatcherQueue.CreateTimer();
        _progressTimer.Interval = TimeSpan.FromSeconds(20);
        _progressTimer.Tick += OnProgressTimerTick;
        _progressTimer.Start();

        await ViewModel.LoadRulesAsync();
        await ViewModel.LoadSettingsAsync();
        await ViewModel.LoadProgressAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.RewardStateChanged -= OnRewardStateChanged;
        ViewModel.DailyGoalReached -= OnDailyGoalReached;
        if (_progressTimer is not null)
        {
            _progressTimer.Stop();
            _progressTimer.Tick -= OnProgressTimerTick;
        }
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _progressTimer;

    /// <summary>A reward earned gets confetti; every other state change gets a bounce of the ring.</summary>
    private void OnRewardStateChanged(object? sender, RewardState state) => DispatcherQueue.TryEnqueue(() =>
    {
        Motion.Pop(HeroRing);
        if (state == RewardState.RewardReady)
        {
            Celebration.Burst();
        }
    });

    private void OnDailyGoalReached(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        Motion.Pop(TodayCard, 1.02);
        Celebration.Burst(0.5);
    });

    private async void OnProgressTimerTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args) => await ViewModel.LoadProgressAsync();

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
