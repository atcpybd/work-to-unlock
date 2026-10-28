using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using WorkToUnlock.Core.Activity;

namespace WorkToUnlock.Infrastructure.Windows;

/// <summary>
/// Foreground application via GetForegroundWindow → GetWindowThreadProcessId → QueryFullProcessImageName.
/// Only the executable path is read; window titles and contents are never captured.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class Win32ForegroundAppMonitor : IForegroundAppMonitor
{
    private int _cachedPid;
    private long _cachedCreation;
    private string? _cachedPath;

    public ForegroundAppInfo? GetForegroundApp()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == 0)
        {
            return null;
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0)
        {
            return null;
        }

        var minimized = NativeMethods.IsIconic(hwnd);
        var path = Resolve((int)pid);
        if (path is null)
        {
            return null;
        }

        // UWP apps are hosted by ApplicationFrameHost; the real app owns a child CoreWindow.
        if (path.EndsWith(@"\ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase)
            && FindHostedProcess(hwnd, pid) is uint hosted
            && Resolve((int)hosted) is { } hostedPath)
        {
            return new ForegroundAppInfo((int)hosted, hostedPath, minimized);
        }

        return new ForegroundAppInfo((int)pid, path, minimized);
    }

    private string? Resolve(int pid)
    {
        if (!ProcessImage.TryGetImage(pid, out var path, out var creation))
        {
            return null;
        }

        if (pid == _cachedPid && creation == _cachedCreation && _cachedPath is not null)
        {
            return _cachedPath;
        }

        _cachedPid = pid;
        _cachedCreation = creation;
        _cachedPath = path;
        return path;
    }

    private static uint? FindHostedProcess(nint frame, uint framePid)
    {
        var state = new HostSearch { FramePid = framePid };
        NativeMethods.EnumChildWindows(frame, &EnumChild, (nint)(&state));
        return state.Found == 0 ? null : state.Found;
    }

    [UnmanagedCallersOnly]
    private static int EnumChild(nint hwnd, nint lParam)
    {
        var state = (HostSearch*)lParam;
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid != 0 && pid != state->FramePid)
        {
            state->Found = pid;
            return 0;
        }

        return 1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HostSearch
    {
        public uint FramePid;
        public uint Found;
    }
}

/// <summary>Idle time from GetLastInputInfo (keyboard and mouse input in this session).</summary>
[SupportedOSPlatform("windows")]
public sealed class Win32IdleDetector(ILogger<Win32IdleDetector> logger) : IIdleDetector
{
    private bool _warned;

    public TimeSpan GetIdleDuration()
    {
        var info = new NativeMethods.LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.LASTINPUTINFO>() };
        if (!NativeMethods.GetLastInputInfo(ref info))
        {
            if (!_warned)
            {
                _warned = true;
                logger.LogWarning("GetLastInputInfo failed; treating the user as idle");
            }

            // Unknown input state never earns work time.
            return TimeSpan.MaxValue;
        }

        // Both values are 32-bit tick counts; unsigned subtraction handles the 49.7-day wrap.
        var idle = unchecked((uint)Environment.TickCount - info.dwTime);
        return TimeSpan.FromMilliseconds(idle);
    }
}

/// <summary>Reads the kernel boot counter so persisted monotonic deadlines are only reused within the same boot.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsBootId
{
    public static string? Read()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters");
            return key?.GetValue("BootId") is int bootId ? bootId.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return null;
        }
    }
}
