# Recovery

## Where things live

| What | Where |
|---|---|
| Database | `%LOCALAPPDATA%\WorkToUnlock\worktounlock.db` (daily backup: `worktounlock.backup.db`) |
| Logs (agent, dashboard) | `%LOCALAPPDATA%\WorkToUnlock\logs\` (14 days) |
| Strict-mode state, service logs, AppLocker backups | `%ProgramData%\WorkToUnlock\` (administrators only) |
| Startup entry | `HKLM\Software\Microsoft\Windows\CurrentVersion\Run\WorkToUnlock` (installed) or the same under HKCU (portable) |

## The agent crashed or is not running

- After a crash, Windows Error Reporting restarts the agent (`RegisterApplicationRestart`, if it had run for at least 60 seconds).
- Opening the dashboard starts the agent if it is not running; the dashboard also has a **Start agent** button.
- At the next sign-in it starts again from the Run entry.
- State is restored from the database. Work credited in the last 15 seconds before a crash may be lost; reward state transitions are never lost because they are saved before they take effect.

## The dashboard crashed

Nothing else is affected. Open it again from the Start menu or the tray.

## Restricted apps are blocked but should not be

1. Check the dashboard: is a reward active? Is enforcement healthy?
2. Turn off *Settings > Enforce restrictions* (asks for a reason; audited).
3. If the dashboard is unavailable, end `WorkToUnlock.Agent.exe` in Task Manager. Process enforcement stops immediately.
4. If strict mode was used, apps may still be blocked by AppLocker; see below.

## Removing AppLocker rules (strict mode)

From an elevated prompt:

```powershell
& "C:\Program Files\Work to Unlock\WorkToUnlock.Service.exe" rollback
```

This removes every rule created by Work to Unlock (identified by id and by the `[Managed by Work to Unlock]` description marker), restores the Executable collection's original enforcement mode, and withdraws the strict-mode consent. Rules that Work to Unlock did not create are not touched.

If the program files are already gone, remove the rules manually: run `secpol.msc`, open *Application Control Policies > AppLocker > Executable Rules*, and delete the rules whose description is `[Managed by Work to Unlock]`. The policy as it was before Work to Unlock first changed it is saved as `%ProgramData%\WorkToUnlock\applocker-backup-<timestamp>.xml` and can be re-imported with `Set-AppLockerPolicy -XmlPolicy <file>`.

When the service is stopped or crashes, its rules stay in place on purpose (restrictions fail safe). The installer configures the service to restart automatically after a failure.

## Database problems

- On start the agent runs an integrity check. A damaged database is renamed to `worktounlock.corrupt-<timestamp>.db` and replaced by the latest backup; if the backup is also damaged, a new empty database is created. The dashboard and the activity log report what happened.
- If the database cannot be opened at all (for example, a full or read-only disk), the agent shows a tray notification and keeps retrying. While it cannot open the database it has no rules, so nothing is tracked or enforced; the dashboard shows the agent as unavailable.
- If writes fail while running, the dashboard shows *Progress is not being saved*; the agent keeps working in memory and retries.

To start over completely: exit the agent (Task Manager), delete `%LOCALAPPDATA%\WorkToUnlock`, and start the dashboard. Default apps are added again on first start.

## Uninstall

Uninstalling (Settings > Apps):

1. closes the agent and dashboard (`WM_CLOSE`, then forced after a timeout);
2. runs `WorkToUnlock.Service.exe rollback` if the strict service was installed (failures never block the uninstall);
3. stops and removes the service, the Run entry, the Start menu shortcut and the program files.

Per-user data in `%LOCALAPPDATA%\WorkToUnlock` and `%ProgramData%\WorkToUnlock` (logs, AppLocker backups) is kept. A portable (zip) copy registered itself under `HKCU\...\Run\WorkToUnlock`; turn off *Start with Windows* before deleting the folder, or delete that registry value.
