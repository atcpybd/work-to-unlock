using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WorkToUnlock.Agent.Windows;

/// <summary>
/// Hidden top-level window on a dedicated STA thread. It receives session notifications (lock, unlock, remote
/// disconnect), power broadcasts (suspend/resume), shutdown/Restart Manager messages, Explorer restarts and tray
/// callbacks. A top-level window is used rather than a message-only window because only top-level windows receive
/// broadcast messages.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe partial class AgentWindow : IDisposable
{
    public const uint WM_APP = 0x8000;
    public const uint WM_TRAY = WM_APP + 1;
    private const uint WM_INVOKE = WM_APP + 2;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_QUERYENDSESSION = 0x0011;
    private const uint WM_ENDSESSION = 0x0016;
    private const uint WM_POWERBROADCAST = 0x0218;
    private const uint WM_WTSSESSION_CHANGE = 0x02B1;
    private const nint PBT_APMSUSPEND = 0x4;
    private const nint PBT_APMRESUMESUSPEND = 0x7;
    private const nint PBT_APMRESUMEAUTOMATIC = 0x12;
    private const uint WS_EX_TOOLWINDOW = 0x80;
    private const uint NOTIFY_FOR_THIS_SESSION = 0;

    private static AgentWindow? s_instance;

    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly ManualResetEventSlim _ready = new();
    private Thread? _thread;
    private uint _taskbarCreated;
    private int _disposed;

    public nint Handle { get; private set; }

    public event Action<uint>? SessionChanged;

    /// <summary>True when the machine is about to sleep, false when it resumed.</summary>
    public event Action<bool>? PowerChanged;

    public event Action? TaskbarCreated;

    /// <summary>Tray callback: (event, x, y) using NOTIFYICON_VERSION_4 semantics.</summary>
    public event Action<uint, int, int>? TrayMessage;

    /// <summary>Windows is shutting down / signing out, or an installer asked the agent to close.</summary>
    /// <summary>Raised when the agent should close. True when Windows is ending the session (sign-out, shutdown, installer).</summary>
    public event Action<bool>? CloseRequested;

    public void Start()
    {
        if (Interlocked.CompareExchange(ref s_instance, this, null) is not null)
        {
            throw new InvalidOperationException("Only one agent window can exist.");
        }

        _thread = new Thread(ThreadMain) { IsBackground = true, Name = "Agent window" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(10)) || Handle == 0)
        {
            throw new InvalidOperationException("The agent window could not be created.");
        }
    }

    /// <summary>Runs an action on the window thread.</summary>
    public void Invoke(Action action)
    {
        if (Handle == 0 || _disposed != 0)
        {
            return;
        }

        _queue.Enqueue(action);
        PostMessageW(Handle, WM_INVOKE, 0, 0);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0 || Handle == 0)
        {
            return;
        }

        var handle = Handle;
        _queue.Enqueue(() =>
        {
            WTSUnRegisterSessionNotification(handle);
            DestroyWindow(handle);
            PostQuitMessage(0);
        });
        PostMessageW(handle, WM_INVOKE, 0, 0);
        _thread?.Join(TimeSpan.FromSeconds(5));
        Interlocked.CompareExchange(ref s_instance, null, this);
    }

    private void ThreadMain()
    {
        var module = GetModuleHandleW(null);
        fixed (char* className = "WorkToUnlock.AgentWindow")
        fixed (char* title = "Work to Unlock Agent")
        {
            var windowClass = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WndProc,
                hInstance = module,
                lpszClassName = className,
            };
            RegisterClassExW(&windowClass);
            Handle = CreateWindowExW(WS_EX_TOOLWINDOW, className, title, 0, 0, 0, 0, 0, 0, 0, module, 0);
        }

        if (Handle == 0)
        {
            _ready.Set();
            return;
        }

        fixed (char* name = "TaskbarCreated")
        {
            _taskbarCreated = RegisterWindowMessageW(name);
        }

        WTSRegisterSessionNotification(Handle, NOTIFY_FOR_THIS_SESSION);
        _ready.Set();

        MSG message;
        while (GetMessageW(&message, 0, 0, 0) > 0)
        {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        try
        {
            if (s_instance is { } self && self.Handle != 0 && hwnd == self.Handle && self.Handle != 0
                && self.TryHandle(message, wParam, lParam, out var result))
            {
                return result;
            }
        }
        catch
        {
            // Exceptions must never cross the native boundary.
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private bool TryHandle(uint message, nint wParam, nint lParam, out nint result)
    {
        result = 0;
        switch (message)
        {
            case WM_INVOKE:
                while (_queue.TryDequeue(out var action))
                {
                    action();
                }

                return true;

            case WM_WTSSESSION_CHANGE:
                SessionChanged?.Invoke((uint)wParam);
                return true;

            case WM_POWERBROADCAST:
                if (wParam == PBT_APMSUSPEND)
                {
                    PowerChanged?.Invoke(true);
                }
                else if (wParam is PBT_APMRESUMEAUTOMATIC or PBT_APMRESUMESUSPEND)
                {
                    PowerChanged?.Invoke(false);
                }

                result = 1;
                return true;

            case WM_TRAY:
                TrayMessage?.Invoke((uint)(lParam & 0xFFFF), (short)(wParam & 0xFFFF), (short)((wParam >> 16) & 0xFFFF));
                return true;

            case WM_QUERYENDSESSION:
                result = 1;
                return true;

            case WM_ENDSESSION:
                if (wParam != 0)
                {
                    CloseRequested?.Invoke(true);
                }

                return true;

            case WM_CLOSE:
                CloseRequested?.Invoke(false);
                return true;

            default:
                if (message == _taskbarCreated && _taskbarCreated != 0)
                {
                    TaskbarCreated?.Invoke();
                    return true;
                }

                return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public delegate* unmanaged<nint, uint, nint, nint, nint> lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public char* lpszMenuName;
        public char* lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int ptX;
        public int ptY;
        public uint lPrivate;
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? moduleName);

    [LibraryImport("user32.dll")]
    private static partial ushort RegisterClassExW(WNDCLASSEXW* windowClass);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint CreateWindowExW(uint exStyle, char* className, char* windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial nint DefWindowProcW(nint hwnd, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial int GetMessageW(MSG* message, nint hwnd, uint filterMin, uint filterMax);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(MSG* message);

    [LibraryImport("user32.dll")]
    private static partial nint DispatchMessageW(MSG* message);

    [LibraryImport("user32.dll")]
    private static partial void PostQuitMessage(int exitCode);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial uint RegisterWindowMessageW(char* name);

    [LibraryImport("wtsapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSRegisterSessionNotification(nint hwnd, uint flags);

    [LibraryImport("wtsapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSUnRegisterSessionNotification(nint hwnd);
}
