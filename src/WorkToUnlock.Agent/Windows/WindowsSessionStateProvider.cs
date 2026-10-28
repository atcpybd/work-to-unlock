using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using WorkToUnlock.Core.Activity;

namespace WorkToUnlock.Agent.Windows;

/// <summary>
/// Tracks lock, Remote Desktop connection and power state from WTS session notifications and power broadcasts.
/// The initial state is read with WTSQuerySessionInformation.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe partial class WindowsSessionStateProvider : ISessionStateProvider
{
    private const uint WTS_CONSOLE_CONNECT = 0x1;
    private const uint WTS_CONSOLE_DISCONNECT = 0x2;
    private const uint WTS_REMOTE_CONNECT = 0x3;
    private const uint WTS_REMOTE_DISCONNECT = 0x4;
    private const uint WTS_SESSION_LOCK = 0x7;
    private const uint WTS_SESSION_UNLOCK = 0x8;
    private const int WTSSessionInfoEx = 25;
    private const int WTSDisconnected = 4;
    private const int WTS_SESSIONSTATE_LOCK = 0;

    private readonly ILogger<WindowsSessionStateProvider> _logger;
    private readonly Lock _lock = new();
    private bool _locked;
    private bool _disconnected;
    private bool _suspended;

    public WindowsSessionStateProvider(ILogger<WindowsSessionStateProvider> logger)
    {
        _logger = logger;
        Refresh();
    }

    public SessionState Current
    {
        get
        {
            lock (_lock)
            {
                return _suspended ? SessionState.Suspended
                    : _disconnected ? SessionState.Disconnected
                    : _locked ? SessionState.Locked
                    : SessionState.Active;
            }
        }
    }

    public event EventHandler<SessionState>? Changed;

    public void Attach(AgentWindow window)
    {
        window.SessionChanged += OnSessionChanged;
        window.PowerChanged += OnPowerChanged;
    }

    private void OnSessionChanged(uint code)
    {
        lock (_lock)
        {
            switch (code)
            {
                case WTS_SESSION_LOCK:
                    _locked = true;
                    break;
                case WTS_SESSION_UNLOCK:
                    _locked = false;
                    break;
                case WTS_CONSOLE_DISCONNECT or WTS_REMOTE_DISCONNECT:
                    _disconnected = true;
                    break;
                case WTS_CONSOLE_CONNECT or WTS_REMOTE_CONNECT:
                    _disconnected = false;
                    break;
            }
        }

        if (code is WTS_CONSOLE_CONNECT or WTS_REMOTE_CONNECT)
        {
            // The lock state after a reconnect depends on how the user signed in; ask Windows.
            Refresh();
        }

        _logger.LogDebug("Session change {Code}: now {State}", code, Current);
        Changed?.Invoke(this, Current);
    }

    private void OnPowerChanged(bool suspending)
    {
        lock (_lock)
        {
            _suspended = suspending;
        }

        _logger.LogInformation(suspending ? "System is suspending" : "System resumed");
        Changed?.Invoke(this, Current);
    }

    private void Refresh()
    {
        if (!WTSQuerySessionInformationW(0, uint.MaxValue, WTSSessionInfoEx, out var buffer, out _) || buffer == 0)
        {
            _logger.LogWarning("Could not query the session state; assuming an active session");
            return;
        }

        try
        {
            // WTSINFOEXW { DWORD Level; union { WTSINFOEX_LEVEL1_W } Data } - the union is 8-byte aligned because
            // LEVEL1 contains LARGE_INTEGER fields, so SessionState is at offset 12 and SessionFlags at 16.
            var level = *(uint*)buffer;
            if (level != 1)
            {
                return;
            }

            var connectState = *(int*)(buffer + 12);
            var flags = *(int*)(buffer + 16);
            lock (_lock)
            {
                _disconnected = connectState == WTSDisconnected;
                _locked = flags == WTS_SESSIONSTATE_LOCK;
            }
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    [LibraryImport("wtsapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSQuerySessionInformationW(nint server, uint sessionId, int infoClass, out nint buffer, out uint bytesReturned);

    [LibraryImport("wtsapi32.dll")]
    private static partial void WTSFreeMemory(nint memory);
}
