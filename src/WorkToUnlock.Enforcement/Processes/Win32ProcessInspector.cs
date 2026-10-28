using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using WorkToUnlock.Infrastructure.Windows;

namespace WorkToUnlock.Enforcement.Processes;

/// <summary>
/// Win32 implementation of <see cref="IProcessInspector"/>. Uses EnumProcesses plus per-process queries with
/// PROCESS_QUERY_LIMITED_INFORMATION, caching identity per (pid, creation time) so a scan stays cheap.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe partial class Win32ProcessInspector(ILogger<Win32ProcessInspector> logger) : IProcessInspector
{
    private const uint PROCESS_TERMINATE = 0x0001;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint SYNCHRONIZE = 0x00100000;
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenUser = 1;
    private const uint WM_CLOSE = 0x0010;
    private const uint GW_OWNER = 4;
    private const int ERROR_ACCESS_DENIED = 5;

    private readonly Dictionary<int, CachedProcess> _cache = [];
    private readonly SecurityIdentifier _currentUser = WindowsIdentity.GetCurrent().User!;

    public int CurrentProcessId { get; } = Environment.ProcessId;

    public int CurrentSessionId { get; } = GetSessionId((uint)Environment.ProcessId);

    public IReadOnlyList<ProcessEntry> Snapshot()
    {
        var pids = EnumerateProcessIds();
        var result = new List<ProcessEntry>(64);
        var alive = new HashSet<int>();

        foreach (var pid in pids)
        {
            if (pid <= 4 || !ProcessIdToSessionId((uint)pid, out var session) || session != (uint)CurrentSessionId)
            {
                continue;
            }

            alive.Add(pid);
            var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
            if (handle == 0)
            {
                continue;
            }

            try
            {
                if (!GetProcessTimes(handle, out var created, out _, out _, out _))
                {
                    continue;
                }

                if (!_cache.TryGetValue(pid, out var cached) || cached.CreationTime != created)
                {
                    var path = ProcessImage.QueryImagePath(handle);
                    if (path is null)
                    {
                        continue;
                    }

                    var (owned, accessible) = GetOwnership(handle);
                    var critical = IsProcessCritical(handle, out var isCritical) && isCritical;
                    cached = new CachedProcess(created, path, owned, accessible, critical);
                    _cache[pid] = cached;
                }

                result.Add(new ProcessEntry(pid, created, cached.Path, (int)session, cached.Owned, cached.Accessible, cached.Critical));
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        foreach (var stale in _cache.Keys.Where(k => !alive.Contains(k)).ToList())
        {
            _cache.Remove(stale);
        }

        return result;
    }

    public int RequestClose(ProcessEntry process)
    {
        var state = new CloseState { ProcessId = (uint)process.ProcessId };
        EnumWindows(&CloseWindowsOfProcess, (nint)(&state));
        return state.Count;
    }

    public TerminationOutcome Terminate(ProcessEntry process)
    {
        var handle = OpenProcess(PROCESS_TERMINATE | PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, false, (uint)process.ProcessId);
        if (handle == 0)
        {
            var error = Marshal.GetLastPInvokeError();
            return error == ERROR_ACCESS_DENIED ? TerminationOutcome.AccessDenied : TerminationOutcome.AlreadyExited;
        }

        try
        {
            // Re-verify the identity: the pid must still be the same executable instance we decided to close.
            if (!GetProcessTimes(handle, out var created, out var exited, out _, out _))
            {
                return TerminationOutcome.Failed;
            }

            if (exited != 0)
            {
                return TerminationOutcome.AlreadyExited;
            }

            var path = ProcessImage.QueryImagePath(handle);
            if (created != process.CreationTime || !string.Equals(path, process.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            {
                return TerminationOutcome.IdentityChanged;
            }

            if (!TerminateProcess(handle, 1))
            {
                return Marshal.GetLastPInvokeError() == ERROR_ACCESS_DENIED ? TerminationOutcome.AccessDenied : TerminationOutcome.Failed;
            }

            WaitForSingleObject(handle, 3000);
            return TerminationOutcome.Terminated;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Terminating pid {Pid} failed", process.ProcessId);
            return TerminationOutcome.Failed;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private (bool Owned, bool Accessible) GetOwnership(nint processHandle)
    {
        if (!OpenProcessToken(processHandle, TOKEN_QUERY, out var token))
        {
            return (false, false);
        }

        try
        {
            GetTokenInformation(token, TokenUser, null, 0, out var needed);
            if (needed == 0)
            {
                return (false, false);
            }

            var buffer = new byte[needed];
            fixed (byte* p = buffer)
            {
                if (!GetTokenInformation(token, TokenUser, p, needed, out _))
                {
                    return (false, false);
                }

                // TOKEN_USER starts with SID_AND_ATTRIBUTES whose first field is the SID pointer.
                var sid = new SecurityIdentifier(*(nint*)p);
                return (sid == _currentUser, true);
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static int[] EnumerateProcessIds()
    {
        var capacity = 1024;
        while (true)
        {
            var buffer = new uint[capacity];
            fixed (uint* p = buffer)
            {
                if (!K32EnumProcesses(p, (uint)(capacity * sizeof(uint)), out var needed))
                {
                    throw new InvalidOperationException($"EnumProcesses failed ({Marshal.GetLastPInvokeError()}).");
                }

                var count = (int)(needed / sizeof(uint));
                if (count < capacity)
                {
                    return buffer.Take(count).Select(id => (int)id).ToArray();
                }
            }

            capacity *= 2;
        }
    }

    private static int GetSessionId(uint pid) => ProcessIdToSessionId(pid, out var session) ? (int)session : -1;

    [UnmanagedCallersOnly]
    private static int CloseWindowsOfProcess(nint hwnd, nint lParam)
    {
        var state = (CloseState*)lParam;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == state->ProcessId && GetWindow(hwnd, GW_OWNER) == 0 && IsWindowVisible(hwnd))
        {
            PostMessageW(hwnd, WM_CLOSE, 0, 0);
            state->Count++;
        }

        return 1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CloseState
    {
        public uint ProcessId;
        public int Count;
    }

    private sealed record CachedProcess(long CreationTime, string Path, bool Owned, bool Accessible, bool Critical);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool K32EnumProcesses(uint* processIds, uint size, out uint bytesReturned);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(nint process, out long creationTime, out long exitTime, out long kernelTime, out long userTime);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(nint process, uint exitCode);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsProcessCritical(nint process, [MarshalAs(UnmanagedType.Bool)] out bool critical);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint desiredAccess, out nint token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(nint token, int informationClass, byte* information, uint length, out uint returnLength);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumWindows(delegate* unmanaged<nint, nint, int> callback, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [LibraryImport("user32.dll")]
    private static partial nint GetWindow(nint hwnd, uint command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);
}
