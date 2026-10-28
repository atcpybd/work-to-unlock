# Testing

## Automated tests

| Project | Platform | Covers |
|---|---|---|
| `WorkToUnlock.Core.Tests` | any | Reward state machine (accumulation, target, non-stacking, activation, expiry, concurrent activation, durability ordering, restart within a boot and after a reboot, clock moved back or forward, sleep, settings changes, reset, invalid persisted state, failed saves); work tracker (approved/unapproved switching, idle boundaries, lock/disconnect/suspend, minimized apps, irregular polling, gaps, double counting, counts-toward-goal, sessions, periodic persistence, persistence failure recovery); rule matching (exact path, renamed copies, publisher scope, untrusted signatures, install folder, helpers, precedence); settings and rule-safety validation; path handling |
| `WorkToUnlock.Agent.Tests` | any | Process enforcer with a fake process inspector (grace period, launch termination, close-only mode, untouchable processes, protected executables, degraded health, multiple instances, rule updates); enforcement coordinator through the reward lifecycle, disabled enforcement and strict-mode fallback; settings audit; IPC request handler validation; notifications and rate limits; SQLite persistence (schema, round trips, optimistic concurrency, statistics, pruning, export, corruption quarantine and restore from backup); AppLocker policy merging |
| `WorkToUnlock.IntegrationTests` | Windows | Real foreground detection, idle detection, boot id, Authenticode signer reading, the Win32 process inspector against a harmless test target (snapshot, owner, graceful close, identity check before termination, apps that ignore `WM_CLOSE`), the process enforcer end to end, pipe ACLs, and an end-to-end run of the real agent executable as an isolated instance (rules over IPC, closing a launched restricted app, audited disable, crash + restart persistence, graceful shutdown on `WM_CLOSE`) |

Integration tests are `[WindowsFact]`s: on other platforms they are reported as **skipped**, never as passed. They only ever close copies of `WorkToUnlock.TestTarget.exe`, and the agent runs with `WORKTOUNLOCK_INSTANCE` and `WORKTOUNLOCK_DATA_DIR` set, so it uses its own pipe, mutex and data folder, skips startup registration and never adds default rules.

Run:

```bash
dotnet test tests/WorkToUnlock.Core.Tests
```

```bash
dotnet test tests/WorkToUnlock.Agent.Tests
```

```bash
dotnet test tests/WorkToUnlock.IntegrationTests
```

## Trying the app without touching your real setup

```powershell
$env:WORKTOUNLOCK_INSTANCE = 'demo'
$env:WORKTOUNLOCK_DATA_DIR = "$env:TEMP\wtu-demo"
.\artifacts\publish\win-x64\WorkToUnlock.App.exe
```

`tools/Send-AgentRequest.ps1 -Instance demo -Method getStatus` talks to that agent from PowerShell.

## Manual Windows test checklist

Not automatable or not yet verified. Use a disposable VM where noted.

### Tracking
- [ ] Work in VS Code for 2 minutes with *Work requirement* set to 1: reward becomes ready and a notification appears.
- [ ] Switch VS Code → Blockbench → VS Code: time keeps counting; switch to a browser: it stops.
- [ ] Stop touching the keyboard and mouse: the clock stops immediately; continuing within the idle threshold adds the pause, staying away longer adds none of it.
- [ ] Lock (Win+L) for a few minutes and unlock: locked time is not counted.
- [ ] Sleep and resume: sleep time is not counted; an active reward keeps elapsing.
- [ ] Remote Desktop: disconnect and reconnect; disconnected time is not counted.
- [ ] Minimize the work app: counting stops.
- [ ] Change the system clock backwards during a reward: the reward does not get longer.

### Enforcement
- [ ] Start Steam while working: it is closed within about a second, with a notification.
- [ ] Activate a reward: Steam and Discord can be started; Discord updates (new `app-x.y.z` folder) still match.
- [ ] Let the reward expire with Steam open: warning notification, then Steam is closed after the grace period.
- [ ] Close-only mode: Steam is asked to close and minimizes to the tray; nothing is terminated.
- [ ] Run Steam as administrator: dashboard shows *Partly protected*.
- [ ] Disable enforcement (requires a reason) and re-enable; both appear in *Settings > Activity log*.

### Agent, dashboard and tray
- [ ] Sign out and in: the agent starts automatically; turning off *Start with Windows* prevents that.
- [ ] Kill the agent in Task Manager, open the dashboard: the agent is restarted.
- [ ] Explorer restart (`taskkill /f /im explorer.exe`, then start it): the tray icon comes back.
- [ ] Tray menu: state lines, *Unlock apps* (when ready), *Open dashboard*, *Settings*, *Exit dashboard*.
- [ ] Close the dashboard with *Minimize to tray* on and off; tracking continues in both cases.
- [ ] Light, dark and system themes; keyboard-only navigation (Tab, arrows, access keys D/A/T/S); Narrator reads headings and buttons.
- [ ] Display scaling 100%, 150% and 200%; narrow window widths.

### Data
- [ ] Export local data and inspect the JSON.
- [ ] Corrupt `worktounlock.db` while the agent is stopped: on start it restores the backup and notifies.

### Installer
- [ ] Install, sign out and in, uninstall on a clean VM: no Run entry, no files in Program Files, no service, agent not running.
- [ ] Upgrade from an older MSI: agent restarted, data kept.
- [ ] Silent install: `msiexec /i WorkToUnlock-1.0.0-x64.msi /qn`.

### Strict mode (Windows Enterprise or Education VM only)
- [ ] Install with *Strict enforcement service*; enable strict mode (UAC); choose *Strict*.
- [ ] Steam cannot start while working; can start during a reward.
- [ ] Existing AppLocker rules are unchanged (`Get-AppLockerPolicy -Local -Xml` before and after).
- [ ] `WorkToUnlock.Service.exe rollback` restores the original policy; so does uninstalling.
- [ ] On Windows Home or Pro, *Enable strict mode* explains that the edition is unsupported.
