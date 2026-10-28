namespace WorkToUnlock.Enforcement.Processes;

/// <summary>A running process as seen by the enforcer.</summary>
/// <param name="CreationTime">FILETIME of process creation; with the pid it identifies the process instance.</param>
/// <param name="OwnedByCurrentUser">True only when the process token's user is the current user.</param>
/// <param name="Accessible">False when the token could not be read (typically an elevated process).</param>
public sealed record ProcessEntry(
    int ProcessId,
    long CreationTime,
    string ExecutablePath,
    int SessionId,
    bool OwnedByCurrentUser,
    bool Accessible,
    bool IsCritical);

public enum TerminationOutcome
{
    Terminated,
    AlreadyExited,
    AccessDenied,

    /// <summary>The pid now belongs to a different process; nothing was done.</summary>
    IdentityChanged,
    Failed,
}

/// <summary>Process operations used by <see cref="ProcessEnforcer"/>. Faked in tests.</summary>
public interface IProcessInspector
{
    int CurrentProcessId { get; }

    int CurrentSessionId { get; }

    /// <summary>Processes in the current interactive session whose executable path could be resolved.</summary>
    IReadOnlyList<ProcessEntry> Snapshot();

    /// <summary>Posts WM_CLOSE to the process's top-level windows. Returns the number of windows asked to close.</summary>
    int RequestClose(ProcessEntry process);

    /// <summary>Terminates the process after re-verifying that the pid still refers to the same executable instance.</summary>
    TerminationOutcome Terminate(ProcessEntry process);
}
