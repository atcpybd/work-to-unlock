using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WorkToUnlock.IntegrationTests;

/// <summary>A fact that only runs on Windows; elsewhere it is reported as skipped.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Requires Windows.";
        }
    }
}

internal static class TestPaths
{
    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    public static string Configuration { get; } = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";

    public static string AgentExecutable => Path.Combine(RepositoryRoot, "src", "WorkToUnlock.Agent", "bin", Configuration, "net10.0", "WorkToUnlock.Agent.exe");

    public static string TestTargetDirectory => Path.Combine(RepositoryRoot, "tests", "WorkToUnlock.TestTarget", "bin", Configuration, "net10.0");

    public static string NewTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "wtu-it", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Copies the test target into its own folder so every test has a unique executable path.</summary>
    public static string CopyTestTarget()
    {
        var directory = NewTempDirectory();
        foreach (var file in Directory.GetFiles(TestTargetDirectory, "WorkToUnlock.TestTarget*"))
        {
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        }

        return Path.Combine(directory, "WorkToUnlock.TestTarget.exe");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WorkToUnlock.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}

/// <summary>A running test target that is always cleaned up.</summary>
internal sealed class TargetProcess : IDisposable
{
    private TargetProcess(Process process) => Process = process;

    public Process Process { get; }

    public int Id => Process.Id;

    public static TargetProcess Start(string executable, bool ignoreClose = false)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        start.ArgumentList.Add("--lifetime");
        start.ArgumentList.Add("60");
        if (ignoreClose)
        {
            start.ArgumentList.Add("--ignore-close");
        }

        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the test target.");
        var target = new TargetProcess(process);
        target.WaitForWindow();
        return target;
    }

    public bool WaitForExit(TimeSpan timeout) => Process.WaitForExit(timeout);

    public void Dispose()
    {
        try
        {
            if (!Process.HasExited)
            {
                Process.Kill();
                Process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
        }

        Process.Dispose();
    }

    private void WaitForWindow()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && !Process.HasExited)
        {
            Process.Refresh();
            if (Process.MainWindowHandle != 0)
            {
                return;
            }

            Thread.Sleep(50);
        }
    }
}

internal static partial class NativeTestMethods
{
    private const uint WM_CLOSE = 0x0010;

    /// <summary>Posts WM_CLOSE to every top-level window of the process with the given class name.</summary>
    public static unsafe int CloseWindowsOfProcess(int processId, string className)
    {
        var count = 0;
        var handles = new List<nint>();
        var gc = System.Runtime.InteropServices.GCHandle.Alloc(handles);
        try
        {
            EnumWindows(&Collect, System.Runtime.InteropServices.GCHandle.ToIntPtr(gc));
        }
        finally
        {
            gc.Free();
        }

        foreach (var hwnd in handles)
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            var buffer = new char[256];
            fixed (char* p = buffer)
            {
                var length = GetClassNameW(hwnd, p, buffer.Length);
                if (pid == processId && new string(buffer, 0, length) == className)
                {
                    PostMessageW(hwnd, WM_CLOSE, 0, 0);
                    count++;
                }
            }
        }

        return count;
    }

    [UnmanagedCallersOnly]
    private static int Collect(nint hwnd, nint state)
    {
        ((List<nint>)System.Runtime.InteropServices.GCHandle.FromIntPtr(state).Target!).Add(hwnd);
        return 1;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool EnumWindows(delegate* unmanaged<nint, nint, int> callback, nint state);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [LibraryImport("user32.dll")]
    private static unsafe partial int GetClassNameW(nint hwnd, char* name, int max);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);
}
