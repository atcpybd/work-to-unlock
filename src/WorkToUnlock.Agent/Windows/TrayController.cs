using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using WorkToUnlock.Agent.Services;
using WorkToUnlock.Core.Activity;
using WorkToUnlock.Core.Domain;
using WorkToUnlock.Core.Enforcement;
using WorkToUnlock.Core.Ipc;
using WorkToUnlock.Core.Rewards;

namespace WorkToUnlock.Agent.Windows;

/// <summary>
/// System tray icon with status, progress and remaining reward time, plus a context menu to unlock a ready reward,
/// open the dashboard or settings, and exit the dashboard. There is deliberately no "exit agent" item: stopping
/// tracking and enforcement is an audited setting, not a menu click.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class TrayController(
    AgentWindow window,
    IRewardEngine engine,
    DashboardBridge dashboards,
    ILogger<TrayController> logger) : ITrayPresenter, IDisposable
{
    private const uint IconId = 1;
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
    private const uint NIIF_INFO = 0x1, NIIF_RESPECT_QUIET_TIME = 0x80;
    private const uint NOTIFYICON_VERSION_4 = 4;
    private const uint WM_CONTEXTMENU = 0x007B, WM_LBUTTONDBLCLK = 0x0203, WM_NULL = 0;
    private const uint NIN_SELECT = 0x400, NIN_KEYSELECT = 0x401, NIN_BALLOONUSERCLICK = 0x405;
    private const uint MF_STRING = 0, MF_GRAYED = 0x1, MF_SEPARATOR = 0x800;
    private const uint TPM_RIGHTBUTTON = 0x2, TPM_RETURNCMD = 0x100, TPM_NONOTIFY = 0x80;
    private const int CmdActivate = 10, CmdOpen = 11, CmdSettings = 12, CmdExitDashboard = 13;

    private readonly Dictionary<string, nint> _icons = [];
    private AgentStatus? _status;
    private string _currentIcon = "working";
    private string _currentTip = "Work to Unlock";
    private bool _added;

    public void Initialize()
    {
        foreach (var name in new[] { "working", "ready", "unlocked", "disabled" })
        {
            _icons[name] = LoadTrayIcon(name);
        }

        window.TrayMessage += OnTrayMessage;
        window.TaskbarCreated += () => window.Invoke(AddIcon);
        window.Invoke(AddIcon);
    }

    public void Update(AgentStatus status)
    {
        _status = status;
        var icon = !status.EnforcementEnabled ? "disabled" : status.Reward.State switch
        {
            RewardState.RewardReady => "ready",
            RewardState.Unlocked => "unlocked",
            _ => "working",
        };
        var tip = Truncate($"Work to Unlock: {DescribeState(status)}", 127);
        if (icon == _currentIcon && tip == _currentTip)
        {
            return;
        }

        _currentIcon = icon;
        _currentTip = tip;
        window.Invoke(() => Modify(NIF_ICON | NIF_TIP | NIF_SHOWTIP, null, null));
    }

    public void ShowNotification(string title, string message) =>
        window.Invoke(() => Modify(NIF_INFO, Truncate(title, 63), Truncate(message, 255)));

    public unsafe void Dispose()
    {
        if (_added)
        {
            var data = CreateData(0);
            Shell_NotifyIconW(NIM_DELETE, &data);
            _added = false;
        }

        foreach (var icon in _icons.Values.Where(i => i != 0))
        {
            DestroyIcon(icon);
        }
    }

    internal static string DescribeState(AgentStatus status)
    {
        if (!status.EnforcementEnabled)
        {
            return "Enforcement disabled";
        }

        var reward = status.Reward;
        return reward.State switch
        {
            RewardState.RewardReady => $"Reward ready ({NotificationService.Format(reward.RewardDuration)})",
            RewardState.Unlocked => $"Unlocked, {NotificationService.Format(reward.RemainingReward)} left",
            _ => $"{(int)reward.AccumulatedWork.TotalMinutes} of {(int)reward.RequiredWork.TotalMinutes} min worked",
        };
    }

    internal static string DescribeActivity(AgentStatus status) => status.Tracking.Status switch
    {
        ActivityStatus.Working => $"Tracking: {status.Tracking.CurrentApplicationName}",
        ActivityStatus.Idle => "Paused: idle",
        ActivityStatus.NotWorkApp => "Paused: not a work app",
        ActivityStatus.Locked => "Paused: PC locked",
        ActivityStatus.Disconnected => "Paused: session disconnected",
        ActivityStatus.Suspended => "Paused: sleeping",
        ActivityStatus.NoForeground => "Paused: no active window",
        _ => "Starting…",
    };

    private unsafe void AddIcon()
    {
        var data = CreateData(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        if (_added)
        {
            Shell_NotifyIconW(NIM_DELETE, &data);
        }

        _added = Shell_NotifyIconW(NIM_ADD, &data);
        if (!_added)
        {
            logger.LogWarning("Could not add the tray icon (the taskbar may not be ready yet)");
            return;
        }

        data.uTimeoutOrVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIconW(NIM_SETVERSION, &data);
    }

    private unsafe void Modify(uint flags, string? title, string? message)
    {
        if (!_added)
        {
            AddIcon();
            if (!_added)
            {
                return;
            }
        }

        var data = CreateData(flags);
        if (title is not null && message is not null)
        {
            Copy(title, data.szInfoTitle, 64);
            Copy(message, data.szInfo, 256);
            data.dwInfoFlags = NIIF_INFO | NIIF_RESPECT_QUIET_TIME;
        }

        Shell_NotifyIconW(NIM_MODIFY, &data);
    }

    private unsafe NOTIFYICONDATAW CreateData(uint flags)
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = window.Handle,
            uID = IconId,
            uFlags = flags,
            uCallbackMessage = AgentWindow.WM_TRAY,
            hIcon = _icons.GetValueOrDefault(_currentIcon),
        };
        Copy(_currentTip, data.szTip, 128);
        return data;
    }

    private void OnTrayMessage(uint trayEvent, int x, int y)
    {
        switch (trayEvent)
        {
            case WM_CONTEXTMENU:
                ShowMenu(x, y);
                break;
            case NIN_SELECT or NIN_KEYSELECT or WM_LBUTTONDBLCLK or NIN_BALLOONUSERCLICK:
                Run(() => dashboards.ShowAsync(null));
                break;
        }
    }

    private void ShowMenu(int x, int y)
    {
        var menu = CreatePopupMenu();
        if (menu == 0)
        {
            return;
        }

        try
        {
            var status = _status;
            if (status is not null)
            {
                Append(menu, MF_STRING | MF_GRAYED, 0, DescribeState(status));
                Append(menu, MF_STRING | MF_GRAYED, 0, DescribeActivity(status));
                Append(menu, MF_SEPARATOR, 0, null);
                if (status.Reward.CanActivate)
                {
                    Append(menu, MF_STRING, CmdActivate, $"Unlock apps for {NotificationService.Format(status.Reward.RewardDuration)}");
                }
            }

            Append(menu, MF_STRING, CmdOpen, "Open dashboard");
            Append(menu, MF_STRING, CmdSettings, "Settings");
            Append(menu, MF_SEPARATOR, 0, null);
            Append(menu, dashboards.IsDashboardRunning ? MF_STRING : MF_STRING | MF_GRAYED, CmdExitDashboard, "Exit dashboard");
            SetMenuDefaultItem(menu, CmdOpen, 0);

            SetForegroundWindow(window.Handle);
            var command = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY, x, y, window.Handle, 0);
            PostMessageW(window.Handle, WM_NULL, 0, 0);

            switch (command)
            {
                case CmdActivate:
                    Run(async () =>
                    {
                        var result = await engine.ActivateRewardAsync(CancellationToken.None).ConfigureAwait(false);
                        logger.LogInformation("Reward activation from tray: {Outcome}", result.Outcome);
                    });
                    break;
                case CmdOpen:
                    Run(() => dashboards.ShowAsync(null));
                    break;
                case CmdSettings:
                    Run(() => dashboards.ShowAsync("settings"));
                    break;
                case CmdExitDashboard:
                    Run(dashboards.ExitDashboardsAsync);
                    break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private void Run(Func<Task> action) => _ = Task.Run(async () =>
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Tray command failed");
        }
    });

    private unsafe nint LoadTrayIcon(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", $"tray-{name}.ico");
        var size = GetSystemMetrics(49 /* SM_CXSMICON */);
        fixed (char* p = path)
        {
            var icon = LoadImageW(0, p, 1 /* IMAGE_ICON */, size, size, 0x10 /* LR_LOADFROMFILE */);
            if (icon == 0)
            {
                logger.LogWarning("Tray icon {Path} could not be loaded", path);
                icon = LoadIconW(0, 32512 /* IDI_APPLICATION */);
            }

            return icon;
        }
    }

    private static unsafe void Append(nint menu, uint flags, int id, string? text)
    {
        fixed (char* p = text)
        {
            AppendMenuW(menu, flags, id, p);
        }
    }

    private static unsafe void Copy(string value, char* destination, int capacity)
    {
        var length = Math.Min(value.Length, capacity - 1);
        for (var i = 0; i < length; i++)
        {
            destination[i] = value[i];
        }

        destination[length] = '\0';
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        public fixed char szTip[128];
        public uint dwState;
        public uint dwStateMask;
        public fixed char szInfo[256];
        public uint uTimeoutOrVersion;
        public fixed char szInfoTitle[64];
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [LibraryImport("shell32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool Shell_NotifyIconW(uint message, NOTIFYICONDATAW* data);

    [LibraryImport("user32.dll")]
    private static unsafe partial nint LoadImageW(nint instance, char* name, uint type, int cx, int cy, uint load);

    [LibraryImport("user32.dll")]
    private static partial nint LoadIconW(nint instance, nint name);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll")]
    private static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool AppendMenuW(nint menu, uint flags, nint id, char* text);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetMenuDefaultItem(nint menu, uint item, uint byPosition);

    [LibraryImport("user32.dll")]
    private static partial int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint parameters);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyMenu(nint menu);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);
}
