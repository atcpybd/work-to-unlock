# Architecture

## Processes

```text
            sign-in (HKLM/HKCU Run)                         optional, admin-enabled
                     │                                                │
          ┌──────────▼──────────┐   per-user pipe (ACL: user)  ┌──────▼───────────────┐
          │ WorkToUnlock.Agent  │◄────────────────────────────►│ WorkToUnlock.App     │
          │ (user, no admin)    │   NDJSON request/response    │ WinUI 3 dashboard    │
          │  - activity sampler │   + status events            └──────────────────────┘
          │  - work tracker     │
          │  - reward engine    │   service pipe (ACL: SYSTEM/Admins own,
          │  - process enforcer │   signed-in users connect; caller must be the
          │  - tray + notices   │   installed agent exe)         ┌──────────────────────┐
          │  - SQLite (%LOCALAPPDATA%)◄───────────────────────────►│ WorkToUnlock.Service │
          └─────────────────────┘                               │ (LocalSystem)        │
                                                                 │ AppLocker rules it owns
                                                                 └──────────────────────┘
```

- The **agent** is the only component that tracks or enforces. The dashboard is a pure client; closing it changes nothing. There is no "exit agent" menu item: stopping enforcement is an audited setting.
- The agent runs at **medium integrity**. Foreground tracking (`GetForegroundWindow`, `GetWindowThreadProcessId`, `QueryFullProcessImageName`), idle detection (`GetLastInputInfo`) and closing the user's own processes need no elevation.
- The **service** never touches the interactive desktop (Session 0 isolation). It only edits the local AppLocker policy on behalf of the user identified from the pipe connection.

## Agent internals

| Component | Responsibility |
|---|---|
| `AgentService` | Bootstrap (database → settings → rules → reward state → enforcement → IPC) and the 500 ms loop: sample, track, expire rewards, publish status every second, hourly maintenance. |
| `ActivitySampler` | One `ActivitySample` per tick: monotonic ms (`GetTickCount64`), UTC, foreground app, idle time, session state. |
| `WorkTracker` | Credits the interval between two consecutive samples only when both qualify and the gap is ≤ 5 s. Time after the last keyboard or mouse input is held back and credited only when input resumes within the idle threshold (or the user switches apps with input); it is dropped at the threshold, on lock, or on a gap, so time away from the PC never counts. Records per-app `WorkSessions`. Persists every 15 s and on session changes. |
| `RewardEngine` | Deterministic state machine. All mutations are serialized; each transition is saved (with audit and reward-history rows, in one transaction) *before* `StateChanged` is raised. |
| `EnforcementCoordinator` | Restrictions on ⇔ enforcement enabled ∧ state ≠ Unlocked ∧ any enabled restricted rule. Drives the process enforcer and, in strict mode, the service. |
| `ProcessEnforcer` | Stage A enforcement; see [enforcement.md](enforcement.md). |
| `AgentWindow` | Hidden top-level window on an STA thread: WTS session notifications, power broadcasts, `TaskbarCreated`, tray callbacks, `WM_ENDSESSION`/`WM_CLOSE`. |
| `TrayController` | `Shell_NotifyIcon` (NOTIFYICON_VERSION_4) with state icons, tooltip, context menu and balloon notifications. |
| `NotificationService` | Reward earned / unlocked / ending in 5 and 1 min / ended, blocked launches (rate-limited per app). |
| `AgentRequestHandler` | The IPC API used by the dashboard. Validates everything with the same rules the agent uses internally. |
| `MaintenanceService` | Daily `VACUUM INTO` backup and data-retention pruning. |

## Time

- **Work time** uses the monotonic clock only. Each interval between samples is credited at most once, so irregular polling never over-counts and wall-clock changes cannot inflate work. Intervals longer than 5 s (suspend, debugger, starvation) are not credited.
- **Reward time** is real elapsed time. While running, the remaining time is `min(monotonic deadline − now, UTC expiry − now, duration)`: moving the clock back cannot extend a reward, moving it forward can only shorten it. The monotonic deadline is persisted together with the kernel `BootId`; after an agent restart in the same boot it is reused, after a reboot only UTC is available and a clock set earlier than the activation time ends the reward. `GetTickCount64` includes sleep, so rewards keep elapsing while the PC sleeps or is locked.

## Reward state machine

| From | Event | To | Side effects |
|---|---|---|---|
| Working | accumulated ≥ requirement | RewardReady | `RewardEarned` audit, history row |
| Working | work credited | Working | in-memory; flushed every 15 s |
| RewardReady | work credited | RewardReady | ignored: rewards do not stack |
| RewardReady | activate | Unlocked | expiry = now + duration (captured), monotonic deadline + boot id |
| Unlocked | activate | Unlocked | rejected (`AlreadyActive`) |
| Unlocked | remaining ≤ 0 | (Expired) → Working | progress reset to 0, restrictions re-applied |
| any | reset progress | Working | audited |
| Working | requirement lowered below accumulated | RewardReady | |

EXPIRED is transient and never persisted. Duplicate activation is prevented by the engine's async lock plus an optimistic `Version` check in the database.

## Persistence

SQLite at `%LOCALAPPDATA%\WorkToUnlock\worktounlock.db`, WAL journal, `synchronous=FULL`. Timestamps are Unix epoch milliseconds (UTC).

| Table | Purpose |
|---|---|
| `ApplicationRules` | Work and restricted apps: path, `IdentityMetadata` (JSON: publisher, install root, match scope, related executables), type, enabled, counts-toward-goal |
| `WorkSessions` | Per-app qualifying stretches (start, end, qualifying ms) |
| `RewardState` | Single row (`Id = 1`) with `Version` for optimistic concurrency |
| `RewardHistory` | Earned / activated / ended per reward (statistics) |
| `Settings` | Key/value; values are JSON |
| `AuditEvents` | Reward transitions, rule and setting changes, enforcement disabled/enabled, data export, recovery |

Migrations are ordered scripts tracked with `PRAGMA user_version`; each runs in its own transaction. On open, `PRAGMA quick_check` runs; a corrupt database is moved aside (`*.corrupt-<timestamp>.db`) and restored from the daily backup, or recreated empty (progress lost, restrictions on).

## IPC

Newline-delimited JSON over `\\.\pipe\WorkToUnlock.Agent.<user SID>`:

```json
{"type":"request","id":7,"name":"activateReward"}
{"type":"response","id":7,"payload":{"outcome":"Activated","status":{...}}}
{"type":"event","name":"status","payload":{...}}
```

The pipe ACL allows only the current user (and denies the NETWORK group). Clients connect with `PipeOptions.CurrentUserOnly`, which verifies that the server runs as the same user. Methods are listed in `Core/Ipc/IpcContracts.cs` (`AgentMethods`). `tools/Send-AgentRequest.ps1` sends a request from PowerShell.

Any process running as the user can talk to the agent, exactly as the user can click in the dashboard. Work to Unlock is a self-control tool for the person who owns the account, not a security boundary against that person.

## Identity of applications

Rules are never matched by process name. A rule matches:

1. its exact executable path; or
2. **PublisherAndFileName**: same file name and the same *trusted* Authenticode signer (survives updates that move the executable); or
3. **InstallFolder**: any executable under the install folder signed by the same trusted publisher, or, for unsigned apps, with a known file name (helpers, updaters).

Signatures are read with `WinVerifyTrust` (no revocation check, no network) and cached per path + size + last-write time. Discovery suggests the install folder (Squirrel `app-x.y.z` folders resolve to their parent), finds related executables, and refuses folders that are too broad (drive roots, Program Files, the user profile, AppData).
