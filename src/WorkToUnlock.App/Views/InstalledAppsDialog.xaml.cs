using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using WorkToUnlock.Core.Ipc;

namespace WorkToUnlock.App.Views;

public sealed partial class InstalledAppsDialog : ContentDialog
{
    private readonly IReadOnlyList<InstalledApplication> _apps;

    public InstalledAppsDialog(IReadOnlyList<InstalledApplication> apps)
    {
        _apps = apps;
        InitializeComponent();
        AppsList.ItemsSource = apps;
    }

    public string? SelectedPath => (AppsList.SelectedItem as InstalledApplication)?.ExecutablePath;

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        AppsList.ItemsSource = query.Length == 0
            ? _apps
            : _apps.Where(a => a.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || a.ExecutablePath.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (a.Publisher?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false)).ToList();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => IsPrimaryButtonEnabled = AppsList.SelectedItem is not null;

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (AppsList.SelectedItem is not null)
        {
            ConfirmedByDoubleTap = true;
            Hide();
        }
    }

    public bool ConfirmedByDoubleTap { get; private set; }
}
