using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WorkToUnlock.App.Services;
using WorkToUnlock.Core.Domain;
using WorkToUnlock.Core.Ipc;
using WorkToUnlock.Infrastructure;

namespace WorkToUnlock.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AgentConnection _agent;
    private readonly DialogService _dialogs;
    private readonly PickerService _pickers;
    private AppSettings _saved = AppSettings.Default;
    private bool _loading;

    public SettingsViewModel(AgentConnection agent, DialogService dialogs, PickerService pickers)
    {
        _agent = agent;
        _dialogs = dialogs;
        _pickers = pickers;
        agent.SettingsChanged += (_, settings) =>
        {
            if (!HasChanges)
            {
                Load(settings);
            }
            else
            {
                _saved = settings;
                EnforcementEnabled = settings.EnforcementEnabled;
            }
        };
        agent.StatusChanged += (_, status) => EnforcementStatus = status.Enforcement.Message;
        agent.ConnectionChanged += async (_, connected) =>
        {
            if (connected && !HasChanges)
            {
                await LoadAsync();
            }
        };
    }

    public ObservableCollection<AuditEventViewModel> AuditEvents { get; } = [];

    /// <summary>Asks the user for a reason before enforcement is disabled. Returns null when cancelled.</summary>
    public Func<Task<string?>>? AskDisableReason { get; set; }

    [ObservableProperty]
    public partial double WorkRequirementMinutes { get; set; } = 60;

    [ObservableProperty]
    public partial double RewardDurationMinutes { get; set; } = 20;

    [ObservableProperty]
    public partial double IdleThresholdSeconds { get; set; } = 60;

    [ObservableProperty]
    public partial double GracefulCloseTimeoutSeconds { get; set; } = 15;

    [ObservableProperty]
    public partial double DailyGoalMinutes { get; set; } = 120;

    [ObservableProperty]
    public partial double DataRetentionDays { get; set; } = 90;

    [ObservableProperty]
    public partial int EnforcementModeIndex { get; set; } = 1;

    [ObservableProperty]
    public partial int ThemeIndex { get; set; }

    [ObservableProperty]
    public partial bool StartWithWindows { get; set; } = true;

    [ObservableProperty]
    public partial bool MinimizeToTray { get; set; } = true;

    [ObservableProperty]
    public partial bool NotificationsEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool PauseBreakWhileLocked { get; set; }

    /// <summary>0 = full break per completed goal, 1 = earn break time as you go.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBalanceMode))]
    public partial int RewardModeIndex { get; set; }

    public bool IsBalanceMode => RewardModeIndex == (int)RewardMode.Balance;

    [ObservableProperty]
    public partial double BalanceCapMinutes { get; set; } = 60;

    [ObservableProperty]
    public partial bool CheckForUpdates { get; set; } = true;

    [ObservableProperty]
    public partial bool EnforcementEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool HasChanges { get; set; }

    [ObservableProperty]
    public partial string? ValidationMessage { get; set; }

    [ObservableProperty]
    public partial bool HasValidationMessage { get; set; }

    [ObservableProperty]
    public partial string EnforcementStatus { get; set; } = string.Empty;

    public string DataLocation => AppPaths.ForCurrentUser().DataDirectory;

    public string Version => typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public static string ServicePath => Path.Combine(AppContext.BaseDirectory, "WorkToUnlock.Service.exe");

    public bool StrictServiceInstalled => File.Exists(ServicePath);

    public async Task LoadAsync()
    {
        try
        {
            if (await _agent.RequestAsync<AppSettings>(AgentMethods.GetSettings) is { } settings)
            {
                Load(settings);
            }

            await LoadAuditAsync();
            ShowValidation(null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Infrastructure.Ipc.IpcException or TimeoutException)
        {
            // Not connected yet: the shell shows the connection banner and this page reloads once connected.
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var requested = _saved with
        {
            WorkRequirementMinutes = ToInt(WorkRequirementMinutes),
            RewardDurationMinutes = ToInt(RewardDurationMinutes),
            IdleThresholdSeconds = ToInt(IdleThresholdSeconds),
            GracefulCloseTimeoutSeconds = ToInt(GracefulCloseTimeoutSeconds),
            DataRetentionDays = ToInt(DataRetentionDays),
            DailyGoalMinutes = ToInt(DailyGoalMinutes),
            EnforcementMode = (EnforcementMode)Math.Clamp(EnforcementModeIndex, 0, 2),
            Theme = (ThemePreference)Math.Clamp(ThemeIndex, 0, 2),
            StartWithWindows = StartWithWindows,
            MinimizeToTray = MinimizeToTray,
            NotificationsEnabled = NotificationsEnabled,
            PauseBreakWhileLocked = PauseBreakWhileLocked,
            RewardMode = (RewardMode)Math.Clamp(RewardModeIndex, 0, 1),
            BalanceCapMinutes = ToInt(BalanceCapMinutes),
            CheckForUpdates = CheckForUpdates,
        };

        // Validate locally first for instant feedback; the agent validates again.
        var errors = SettingsValidator.Validate(requested);
        if (errors.Count > 0)
        {
            ShowValidation(string.Join(Environment.NewLine, errors.Select(e => e.Message)));
            return;
        }

        if (!requested.StartWithWindows && _saved.StartWithWindows
            && !await _dialogs.ConfirmAsync("Turn off Start with Windows?", "Tracking and enforcement will not run after you sign in until you open Work to Unlock.", "Turn off"))
        {
            StartWithWindows = true;
            return;
        }

        try
        {
            var result = await _agent.RequestAsync<OperationResult>(AgentMethods.UpdateSettings, requested);
            if (result is { Succeeded: true })
            {
                ShowValidation(null);
                Load(requested);
                await LoadAuditAsync();
            }
            else
            {
                ShowValidation(result is null ? "No response from the agent." : string.Join(Environment.NewLine, new[] { result.Message }.Concat(result.Errors.Select(e => e.Message)).Where(m => !string.IsNullOrEmpty(m))));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Infrastructure.Ipc.IpcException or TimeoutException)
        {
            ShowValidation(ex.Message);
        }
    }

    [RelayCommand]
    private void Revert()
    {
        Load(_saved);
        ShowValidation(null);
    }

    /// <summary>Called by the enforcement toggle. Disabling needs confirmation and a reason; both changes are audited.</summary>
    public async Task SetEnforcementEnabledAsync(bool enabled)
    {
        if (_loading || enabled == _saved.EnforcementEnabled)
        {
            return;
        }

        var reason = string.Empty;
        if (!enabled)
        {
            reason = AskDisableReason is null ? null! : await AskDisableReason() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(reason))
            {
                SetEnforcementToggle(true);
                return;
            }
        }

        try
        {
            var result = await _agent.RequestAsync<OperationResult>(AgentMethods.SetEnforcementEnabled, new SetEnforcementRequest(enabled, reason));
            if (result is not { Succeeded: true })
            {
                SetEnforcementToggle(_saved.EnforcementEnabled);
                await _dialogs.ShowMessageAsync("Could not change enforcement", result?.Message ?? "No response from the agent.");
                return;
            }

            _saved = _saved with { EnforcementEnabled = enabled };
            await LoadAuditAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Infrastructure.Ipc.IpcException or TimeoutException)
        {
            SetEnforcementToggle(_saved.EnforcementEnabled);
            await _dialogs.ShowMessageAsync("Could not change enforcement", ex.Message);
        }
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        var path = await _pickers.PickSaveJsonAsync($"work-to-unlock-export-{DateTime.Now:yyyy-MM-dd}");
        if (path is null)
        {
            return;
        }

        try
        {
            var export = await _agent.RequestAsync<ExportResult>(AgentMethods.ExportData, timeout: TimeSpan.FromMinutes(2));
            await File.WriteAllTextAsync(path, export?.Json ?? "{}");
            await _dialogs.ShowMessageAsync("Export complete", $"Your local activity data was saved to {path}.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or Infrastructure.Ipc.IpcException or TimeoutException)
        {
            await _dialogs.ShowMessageAsync("Export failed", ex.Message);
        }
    }

    [RelayCommand]
    private async Task ResetProgressAsync()
    {
        if (!await _dialogs.ConfirmAsync("Reset progress?", "Accumulated work time goes back to zero. A ready or active reward is cancelled and restricted apps are locked again.", "Reset", destructive: true))
        {
            return;
        }

        try
        {
            await _agent.RequestAsync<OperationResult>(AgentMethods.ResetProgress, new ResetProgressRequest("Reset from Settings"));
            await LoadAuditAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Infrastructure.Ipc.IpcException or TimeoutException)
        {
            await _dialogs.ShowMessageAsync("Reset failed", ex.Message);
        }
    }

    [RelayCommand]
    private async Task EnableStrictModeAsync()
    {
        if (!await _dialogs.ConfirmAsync("Enable strict mode?",
                "Strict mode uses Windows AppLocker, managed by the optional Work to Unlock service, to block restricted apps from starting. " +
                "It needs administrator approval, only works on Windows editions that enforce AppLocker, and never changes AppLocker rules it does not own. " +
                "You can turn it off again here or by running the service with 'rollback'.",
                "Continue as administrator"))
        {
            return;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(ServicePath, "consent --enable") { UseShellExecute = true, Verb = "runas" });
            if (process is not null)
            {
                await process.WaitForExitAsync();
                await _dialogs.ShowMessageAsync(process.ExitCode == 0 ? "Strict mode enabled" : "Strict mode not enabled",
                    process.ExitCode == 0
                        ? "Strict mode is now allowed on this PC. Choose 'Strict' as the enforcement mode and save."
                        : "Strict mode could not be enabled. This Windows edition may not support AppLocker; see the service log for details.");
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The UAC prompt was declined.
        }
    }

    private async Task LoadAuditAsync()
    {
        var events = await _agent.RequestAsync<AuditEventList>(AgentMethods.GetAuditEvents, new CountRequest(50));
        AuditEvents.Clear();
        foreach (var e in events?.Events ?? [])
        {
            AuditEvents.Add(new AuditEventViewModel(e.TimestampUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), e.EventType, e.Description, e.Result));
        }
    }

    private void Load(AppSettings settings)
    {
        _loading = true;
        _saved = settings;
        WorkRequirementMinutes = settings.WorkRequirementMinutes;
        RewardDurationMinutes = settings.RewardDurationMinutes;
        IdleThresholdSeconds = settings.IdleThresholdSeconds;
        GracefulCloseTimeoutSeconds = settings.GracefulCloseTimeoutSeconds;
        DataRetentionDays = settings.DataRetentionDays;
        DailyGoalMinutes = settings.DailyGoalMinutes;
        EnforcementModeIndex = (int)settings.EnforcementMode;
        ThemeIndex = (int)settings.Theme;
        StartWithWindows = settings.StartWithWindows;
        MinimizeToTray = settings.MinimizeToTray;
        NotificationsEnabled = settings.NotificationsEnabled;
        PauseBreakWhileLocked = settings.PauseBreakWhileLocked;
        RewardModeIndex = (int)settings.RewardMode;
        BalanceCapMinutes = settings.BalanceCapMinutes;
        CheckForUpdates = settings.CheckForUpdates;
        EnforcementEnabled = settings.EnforcementEnabled;
        HasChanges = false;
        _loading = false;
    }

    private void SetEnforcementToggle(bool value)
    {
        _loading = true;
        EnforcementEnabled = value;
        _loading = false;
    }

    private void ShowValidation(string? message)
    {
        ValidationMessage = message;
        HasValidationMessage = !string.IsNullOrEmpty(message);
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_loading && e.PropertyName is nameof(WorkRequirementMinutes) or nameof(RewardDurationMinutes) or nameof(IdleThresholdSeconds)
                or nameof(GracefulCloseTimeoutSeconds) or nameof(DataRetentionDays) or nameof(DailyGoalMinutes) or nameof(EnforcementModeIndex) or nameof(ThemeIndex)
                or nameof(StartWithWindows) or nameof(MinimizeToTray) or nameof(NotificationsEnabled) or nameof(PauseBreakWhileLocked)
                or nameof(RewardModeIndex) or nameof(BalanceCapMinutes) or nameof(CheckForUpdates))
        {
            HasChanges = true;
        }
    }

    private static int ToInt(double value) => double.IsNaN(value) ? -1 : (int)Math.Round(value);
}

public sealed record AuditEventViewModel(string When, string EventType, string Description, string Result);
