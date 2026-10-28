using System.Runtime.InteropServices;

namespace WorkToUnlock.TestTarget;

/// <summary>
/// Opens a tiny top-level window and waits. WM_CLOSE exits the process, unless started with --ignore-close
/// (simulating apps such as Steam or Discord that minimize to the tray instead of exiting).
/// Exits by itself after --lifetime seconds (default 120) so a failed test never leaves it running.
/// </summary>
internal static unsafe partial class Program
{
    private const uint WM_CLOSE = 0x0010, WM_DESTROY = 0x0002, WM_TIMER = 0x0113;
    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000, WS_VISIBLE = 0x10000000;
    private static bool s_ignoreClose;

    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            return 1;
        }

        s_ignoreClose = args.Contains("--ignore-close");
        var lifetime = 120u;
        var index = Array.IndexOf(args, "--lifetime");
        if (index >= 0 && index + 1 < args.Length && uint.TryParse(args[index + 1], out var seconds))
        {
            lifetime = seconds;
        }

        var module = GetModuleHandleW(null);
        fixed (char* className = "WorkToUnlock.TestTarget")
        fixed (char* title = "Work to Unlock test target")
        {
            var windowClass = new WNDCLASSEXW { cbSize = (uint)sizeof(WNDCLASSEXW), lpfnWndProc = &WndProc, hInstance = module, lpszClassName = className };
            RegisterClassExW(&windowClass);
            var hwnd = CreateWindowExW(0, className, title, WS_OVERLAPPEDWINDOW | WS_VISIBLE, 40, 40, 240, 120, 0, 0, module, 0);
            if (hwnd == 0)
            {
                return 2;
            }

            SetTimer(hwnd, 1, lifetime * 1000, 0);
        }

        MSG message;
        while (GetMessageW(&message, 0, 0, 0) > 0)
        {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }

        return 0;
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case WM_CLOSE when s_ignoreClose:
                return 0;
            case WM_TIMER:
                DestroyWindow(hwnd);
                return 0;
            case WM_DESTROY:
                PostQuitMessage(0);
                return 0;
            default:
                return DefWindowProcW(hwnd, message, wParam, lParam);
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
    private static partial nint GetModuleHandleW(string? name);

    [LibraryImport("user32.dll")]
    private static partial ushort RegisterClassExW(WNDCLASSEXW* windowClass);

    [LibraryImport("user32.dll")]
    private static partial nint CreateWindowExW(uint exStyle, char* className, char* name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")]
    private static partial nint DefWindowProcW(nint hwnd, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial void PostQuitMessage(int code);

    [LibraryImport("user32.dll")]
    private static partial nuint SetTimer(nint hwnd, nuint id, uint elapse, nint callback);

    [LibraryImport("user32.dll")]
    private static partial int GetMessageW(MSG* message, nint hwnd, uint min, uint max);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(MSG* message);

    [LibraryImport("user32.dll")]
    private static partial nint DispatchMessageW(MSG* message);
}
