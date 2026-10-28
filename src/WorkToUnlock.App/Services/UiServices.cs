using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace WorkToUnlock.App.Services;

/// <summary>Human-friendly durations.</summary>
public static class Format
{
    public static string Duration(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
        {
            time = TimeSpan.Zero;
        }

        var minutes = (int)Math.Floor(time.TotalMinutes);
        return minutes switch
        {
            0 when time.TotalSeconds >= 1 => "< 1 min",
            0 => "0 min",
            < 60 => $"{minutes} min",
            _ when minutes % 60 == 0 => $"{minutes / 60} h",
            _ => $"{minutes / 60} h {minutes % 60:D2} min",
        };
    }

    /// <summary>Title-case form used on buttons: "20 Minutes", "1 Hour", "1 Hour 30 Minutes".</summary>
    public static string LongDuration(TimeSpan time)
    {
        var minutes = Math.Max(0, (int)Math.Round(time.TotalMinutes));
        var hours = minutes / 60;
        var rest = minutes % 60;
        var hourText = hours == 1 ? "1 Hour" : $"{hours} Hours";
        var minuteText = rest == 1 ? "1 Minute" : $"{rest} Minutes";
        return hours == 0 ? minuteText : rest == 0 ? hourText : $"{hourText} {minuteText}";
    }

    /// <summary>Minutes, rounding up, for remaining-time messages ("unlock in 12 min").</summary>
    public static string DurationCeiling(TimeSpan time) => Duration(TimeSpan.FromMinutes(Math.Ceiling(Math.Max(0, time.TotalMinutes))));

    /// <summary>Clock-style time for elapsed work and countdowns: "mm:ss", or "h:mm:ss" from one hour.</summary>
    public static string Countdown(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
        {
            time = TimeSpan.Zero;
        }

        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss", CultureInfo.CurrentCulture)
            : time.ToString(@"mm\:ss", CultureInfo.CurrentCulture);
    }

    public static string LocalTime(DateTimeOffset? utc) =>
        utc?.ToLocalTime().ToString("t", CultureInfo.CurrentCulture) ?? string.Empty;
}

/// <summary>Content dialogs anchored to the main window.</summary>
public sealed class DialogService
{
    public XamlRoot? XamlRoot { get; set; }

    public async Task<bool> ConfirmAsync(string title, string message, string confirm, bool destructive = false)
    {
        var dialog = Create(title, confirm, "Cancel");
        dialog.Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
        if (destructive)
        {
            dialog.DefaultButton = ContentDialogButton.Close;
        }

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public async Task ShowMessageAsync(string title, string message)
    {
        var dialog = Create(title, null, "OK");
        dialog.Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
        await dialog.ShowAsync();
    }

    public ContentDialog Create(string title, string? primary, string close)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            CloseButtonText = close,
            XamlRoot = XamlRoot,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
            DefaultButton = primary is null ? ContentDialogButton.Close : ContentDialogButton.Primary,
            RequestedTheme = (XamlRoot?.Content as FrameworkElement)?.RequestedTheme ?? ElementTheme.Default,
        };
        if (primary is not null)
        {
            dialog.PrimaryButtonText = primary;
        }

        return dialog;
    }
}

/// <summary>File pickers for an unpackaged desktop window.</summary>
public sealed class PickerService
{
    public nint WindowHandle { get; set; }

    public async Task<string?> PickExecutableAsync()
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder, ViewMode = PickerViewMode.List };
        picker.FileTypeFilter.Add(".exe");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    public async Task<string?> PickSaveJsonAsync(string suggestedName)
    {
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = suggestedName };
        picker.FileTypeChoices.Add("JSON", [".json"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }
}

/// <summary>Application icons extracted through the shell thumbnail cache.</summary>
public sealed class IconCache
{
    private readonly ConcurrentDictionary<string, Task<BitmapImage?>> _icons = new(StringComparer.OrdinalIgnoreCase);

    public Task<BitmapImage?> GetAsync(string executablePath) => _icons.GetOrAdd(executablePath, LoadAsync);

    private static async Task<BitmapImage?> LoadAsync(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var file = await StorageFile.GetFileFromPathAsync(path);
            using var thumbnail = await file.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.SingleItem, 64);
            if (thumbnail is null)
            {
                return null;
            }

            var image = new BitmapImage();
            await image.SetSourceAsync(thumbnail);
            return image;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }
}
