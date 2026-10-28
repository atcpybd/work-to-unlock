using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WorkToUnlock.App.Services;
using WorkToUnlock.Core.Domain;
using WorkToUnlock.Core.Ipc;

namespace WorkToUnlock.App.ViewModels;

public sealed partial class ShellViewModel : ObservableObject
{
    private readonly AgentConnection _agent;

    public ShellViewModel(AgentConnection agent)
    {
        _agent = agent;
        agent.ConnectionChanged += (_, connected) => IsConnected = connected;
        agent.SettingsChanged += (_, settings) => Settings = settings;
        agent.ConnectionChanged += async (_, connected) =>
        {
            if (connected)
            {
                try
                {
                    Settings = await _agent.RequestAsync<AppSettings>(AgentMethods.GetSettings) ?? Settings;
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or Infrastructure.Ipc.IpcException or TimeoutException)
                {
                }
            }
        };
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDisconnected))]
    public partial bool IsConnected { get; set; }

    public bool IsDisconnected => !IsConnected;

    [ObservableProperty]
    public partial AppSettings Settings { get; set; } = AppSettings.Default;

    [RelayCommand]
    private void StartAgent() => _agent.LaunchAgent();
}
