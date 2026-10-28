# Enforcement

Restricted applications are unavailable in the **Working** and **Reward ready** states and available while a reward is **Unlocked**. Enforcement can be turned off only through *Settings > Enforce restrictions*, which asks for a reason and writes an `EnforcementDisabled` audit event.

## Stage A: process enforcement (default)

Implemented by `ProcessEnforcer` in the agent, running as the signed-in user.

Every second while restrictions are active:

1. Enumerate processes (`EnumProcesses`), keep those in the user's session, and read each one's image path, creation time and token owner. Results are cached per (pid, creation time).
2. Match each path against the enabled restricted rules (see the identity rules in [architecture.md](architecture.md)).
3. Skip anything that is not safe to touch: the agent itself, critical processes, other users' processes, other sessions, anything under `%WINDIR%`, Work to Unlock's own folder, and a list of protected executables (explorer, csrss, winlogon, svchost, dwm, taskmgr, msiexec, Settings, Control Panel, MMC, regedit, cmd and PowerShell, …).
4. Act:

| Situation | Close only | Close, then terminate (default) |
|---|---|---|
| App was already running when restrictions began (reward expired, agent started) | `WM_CLOSE` to its top-level windows, repeated every 30 s | `WM_CLOSE`, then `TerminateProcess` after the graceful-close timeout (default 15 s) |
| App started while restricted (a launch attempt) | `WM_CLOSE` | terminated as soon as it is detected |

Before terminating, the enforcer re-opens the process and checks that the pid still has the same creation time and image path; if not, nothing is done. Steam and Discord minimize to the tray on `WM_CLOSE`, so in close-only mode they keep running in the background.

Notifications: "*X is locked. Work N min more to unlock it*" on a blocked launch (at most once a minute per app), and "*X will be closed in 15 seconds. Save your progress now*" when a reward ends.

### What Stage A does not guarantee

- **Launches are not prevented.** A restricted app can run for up to one scan interval (about 1 s) before it is closed.
- **Elevated apps** (run as administrator) cannot be closed by a medium-integrity process. The dashboard shows *Partly protected* with the reason.
- **The agent can be stopped** by the user (Task Manager). Windows Error Reporting restarts it after a crash, and the dashboard restarts it when opened, but a deliberate kill leaves restrictions off until the next sign-in or dashboard launch.

Health is reported honestly in the dashboard and tray: *Protected*, *Partly protected* (with problems listed), *Not protected* (the scan itself is failing), *Not restricting* (reward active, enforcement disabled or nothing configured).

## Stage B: strict mode (optional, AppLocker)

Strict mode adds **launch prevention** with Windows AppLocker, managed by `WorkToUnlock.Service` (LocalSystem). It is off by default at three levels:

1. The **Strict enforcement service** installer feature is not selected by default.
2. Even when installed, the service refuses to change anything until an administrator runs *Settings > Enable strict mode…* (UAC prompt), which executes `WorkToUnlock.Service.exe consent --enable`. That command first checks compatibility and refuses on unsupported editions.
3. The user must then choose **Strict (AppLocker)** as the enforcement mode. The agent checks with the service before accepting it.

### Compatibility

`AppLockerCompatibility.Check()` requires a Windows edition that enforces locally managed AppLocker policies (Enterprise, Education, IoT Enterprise, Server) and the Application Identity service (`AppIDSvc`). Windows Home and Pro are reported as unsupported. Microsoft documents Group Policy management of AppLocker only for Enterprise, Education and Server, so strict mode does not pretend to work elsewhere.

### What the service changes

For the requesting user (SID taken from the pipe connection, never from the request), the service:

- adds **path deny rules** to the *Executable* rule collection, scoped to that user's SID: `<install folder>\*` for install-folder rules, the exact path otherwise. AppLocker evaluates deny rules before allow rules.
- if, and only if, the Executable collection had **no rules of its own**, adds one owned **allow-all** rule (`*` for Everyone) so that nothing except the restricted apps is blocked once the collection is enforced;
- sets the Executable collection to **Enabled** if it was *Not configured*. An *Audit only* collection is left alone and reported (apps are logged, not blocked).
- starts `AppIDSvc` if it is not running.

Everything else (other rule collections, every rule it does not own, Group Policy from a domain) is left untouched. The service only writes the *local* policy (`Set-AppLockerPolicy` with the complete merged policy) and never uses executable-name-only deny rules.

During a reward the agent asks the service to remove the user's deny rules; when the reward ends they are re-added.

### Ownership and rollback

- Owned rules have deterministic ids and the description marker `[Managed by Work to Unlock]`.
- `%ProgramData%\WorkToUnlock\applocker-owned.json` (SYSTEM/Administrators only) records the owned rule ids per user, the baseline allow rule, and the Executable collection's original enforcement mode.
- Before the first change, the complete original policy is saved to `%ProgramData%\WorkToUnlock\applocker-backup-<timestamp>.xml`.
- `WorkToUnlock.Service.exe rollback` removes every owned rule (by id and by marker, so it also works if the ownership file is lost) and restores the original enforcement mode exactly. The uninstaller runs it automatically.
- When the service stops, rules are deliberately left in place (fail safe). See [recovery.md](recovery.md).

### Verification status

The AppLocker merge logic is covered by unit tests (preserving administrator rules, exact restoration, per-user independence, idempotence, recovery by marker, audit-only handling). Applying policies on a real Enterprise or Education machine has **not** been verified in this repository's environment (developed on Windows 11 Home, where strict mode correctly reports itself unsupported). Test on a virtual machine before relying on it; see the checklist in [testing.md](testing.md).
