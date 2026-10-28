using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WorkToUnlock.App.Services;
using WorkToUnlock.Core.Ipc;
using WorkToUnlock.Core.Updates;
using WorkToUnlock.Infrastructure.Updates;

namespace WorkToUnlock.App.ViewModels;

/// <summary>
/// Update status from the agent's daily check. "Update now" downloads the installer from the project's GitHub
/// release, checks it against the release checksums, and runs it (Windows asks for permission).
/// </summary>
public sealed partial class UpdatesViewModel : ObservableObject
{
    private readonly AgentConnection _agent;
    private readonly DialogService _dialogs;
    private UpdateInfo? _info;

    public UpdatesViewModel(AgentConnection agent, DialogService dialogs)
    {
        _agent = agent;
        _dialogs = dialogs;
        agent.ConnectionChanged += async (_, connected) =>
        {
            if (connected)
            {
                await LoadAsync();
            }
        };
    }

    [ObservableProperty]
    public partial bool IsUpdateAvailable { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? ProgressText { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckNowCommand), nameof(InstallCommand))]
    public partial bool IsBusy { get; set; }

    private bool CanRun => !IsBusy;

    public async Task LoadAsync()
    {
        if (!_agent.IsConnected)
        {
            return;
        }

        try
        {
            Apply(await _agent.RequestAsync<UpdateInfo>(AgentMethods.GetUpdateInfo, new UpdateCheckRequest(false)));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Infrastructure.Ipc.IpcException or TimeoutException)
        {
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task CheckNowAsync()
    {
        IsBusy = true;
        StatusText = "Checking…";
        try
        {
            Apply(await _agent.RequestAsync<UpdateInfo>(AgentMethods.GetUpdateInfo, new UpdateCheckRequest(true)));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Infrastructure.Ipc.IpcException or TimeoutException)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task InstallAsync()
    {
        if (_info is not { IsUpdateAvailable: true } info
            || !await _dialogs.ConfirmAsync(
                $"Install Work to Unlock {info.LatestVersion}?",
                "The installer is downloaded from the project's GitHub releases and checked before it runs. Windows asks for permission, then Work to Unlock closes and starts again with the new version. Your settings, apps and history are kept.",
                "Update now"))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var progress = new Progress<double>(p => ProgressText = $"Downloading… {p.ToString("P0", CultureInfo.CurrentCulture)}");
            var path = await UpdateDownloader.DownloadVerifiedAsync(info, Path.Combine(Path.GetTempPath(), "WorkToUnlock-update"), progress, CancellationToken.None);
            ProgressText = "Installing… Work to Unlock closes and starts again when it's done.";
            Process.Start(new ProcessStartInfo("msiexec.exe", $"/i \"{path}\" /passive /norestart") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or IOException or TaskCanceledException or System.ComponentModel.Win32Exception)
        {
            ProgressText = null;
            await _dialogs.ShowMessageAsync("Update not installed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenReleaseNotes() =>
        Process.Start(new ProcessStartInfo((_info?.ReleasePage ?? UpdateSource.ReleasesPage).ToString()) { UseShellExecute = true });

    private void Apply(UpdateInfo? info)
    {
        if (info is null)
        {
            return;
        }

        _info = info;
        IsUpdateAvailable = info.IsUpdateAvailable;
        Title = info.IsUpdateAvailable ? $"Work to Unlock {info.LatestVersion} is available" : string.Empty;
        StatusText = info switch
        {
            { Error: { } error } => $"{error} You have version {info.CurrentVersion}.",
            { IsUpdateAvailable: true } => $"Version {info.LatestVersion} is available. You have {info.CurrentVersion}.",
            { CheckedUtc: { } when } => $"You're up to date ({info.CurrentVersion}). Last checked at {when.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)}.",
            _ => $"You have version {info.CurrentVersion}.",
        };
    }
}
