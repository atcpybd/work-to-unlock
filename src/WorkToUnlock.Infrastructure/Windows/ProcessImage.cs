using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WorkToUnlock.Infrastructure.Windows;

/// <summary>Resolves process executable paths with the least privilege that works for any process of the user.</summary>
[SupportedOSPlatform("windows")]
public static unsafe class ProcessImage
{
    /// <summary>Returns the full Win32 image path of a process, or null if it exited or cannot be queried.</summary>
    public static string? TryGetImagePath(int processId) => TryGetImage(processId, out var path, out _) ? path : null;

    /// <summary>Returns the image path and creation time (FILETIME) used to detect process-id reuse.</summary>
    public static bool TryGetImage(int processId, out string? path, out long creationTime)
    {
        path = null;
        creationTime = 0;
        if (processId <= 4)
        {
            return false;
        }

        var handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (handle == 0)
        {
            return false;
        }

        try
        {
            path = QueryImagePath(handle);
            if (path is null)
            {
                return false;
            }

            if (NativeMethods.GetProcessTimes(handle, out var created, out _, out _, out _))
            {
                creationTime = created;
            }

            return true;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    /// <summary>Image path for an open process handle (needs PROCESS_QUERY_LIMITED_INFORMATION).</summary>
    public static string? QueryImagePath(nint processHandle)
    {
        var capacity = 1024u;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var buffer = new char[capacity];
            var size = capacity;
            fixed (char* p = buffer)
            {
                if (NativeMethods.QueryFullProcessImageNameW(processHandle, 0, p, ref size))
                {
                    return new string(buffer, 0, (int)size);
                }
            }

            if (Marshal.GetLastPInvokeError() != NativeMethods.ERROR_INSUFFICIENT_BUFFER)
            {
                return null;
            }

            capacity = 32_768;
        }

        return null;
    }
}
