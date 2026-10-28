# Implementation plan and status

Status as of 2026-10-28. Development machine: Windows 11 Home (build 26200, x64), .NET SDK 10.0.403, Windows App SDK 2.6.4, WiX 6.0.2. No Visual Studio; everything is built with the `dotnet` CLI.

Legend: ✅ done and verified on Windows · 🧪 done, covered by automated tests only · 🔲 needs manual verification · ⏭ not done

## Phase 1: Foundation ✅

- ✅ Solution, project structure, central package management, `global.json`
- ✅ Domain models, settings validation, rule safety validation
- ✅ Reward state machine (`RewardEngine`) with durable transitions, optimistic concurrency, monotonic + UTC time
- ✅ SQLite persistence with migrations, integrity check, quarantine and restore from backup, statistics, pruning, export
- ✅ Serilog file logging
- ✅ Unit tests: 94 in `Core.Tests`, 49 in `Agent.Tests`. Both are portable; nothing in them needs Windows.

Acceptance: reward logic and persistence pass automated tests without Windows dependencies. **Met.**

## Phase 2: Real activity tracking ✅

- ✅ Foreground app detection (`GetForegroundWindow` → `GetWindowThreadProcessId` → `QueryFullProcessImageName`, UWP frame host resolution, minimized check)
- ✅ Idle detection (`GetLastInputInfo`, wrap-safe)
- ✅ Session monitoring (WTS lock/unlock, console/remote connect/disconnect, power suspend/resume)
- ✅ Executable identity: path, Authenticode signer via `WinVerifyTrust`, install folder, related executables
- ✅ Qualifying-time accumulation and periodic persistence (`WorkTracker`)
- 🔲 Manual: lock/unlock, sleep/resume, Remote Desktop and clock-change scenarios (see [testing.md](testing.md))

Acceptance: only qualifying foreground time is counted and progress survives restarts. Covered by unit tests plus the end-to-end crash/restart integration test. Real-world lock, sleep and RDP behaviour needs the manual checklist.

## Phase 3: Application enforcement ✅

- ✅ Restricted-app discovery (installed apps from the registry, VS Code/Blockbench/Steam/Discord defaults, Squirrel folders)
- ✅ Process monitoring (`EnumProcesses` + cached per-process identity and ownership)
- ✅ Graceful shutdown (`WM_CLOSE`) and forced termination with identity re-verification
- ✅ Integration with reward states, expiry while a restricted app is running, relaunch handling
- ✅ Integration tests against a harmless test target, including the real agent end to end

Acceptance: test applications are restricted outside reward periods and allowed during rewards. **Met** for the "outside reward" half end to end with the real agent; the "allowed during rewards" path is covered by enforcer integration tests (`RemoveRestrictionsAsync`) and unit tests, because an end-to-end reward needs real foreground work.

## Phase 4: Desktop UI ✅

- ✅ WinUI 3 dashboard: circular progress, current activity, remaining work, reward countdown, unlock button, enforcement health, restricted app status
- ✅ Applications page: work and restricted lists, icons, paths, signer and match scope, today/week time, enable toggles, counts-toward-goal, revalidate, change location, remove; add by browsing or from installed apps with an identity preview
- ✅ Statistics page: today, week, rewards earned/activated, 14-day chart, time per app
- ✅ Settings page: every setting from the brief, audited enforcement toggle with reason, strict-mode consent, export, reset, activity log
- ✅ Tray icon with state icons, tooltip, menu and notifications (owned by the agent so it exists whenever tracking runs)
- ✅ Light/dark/system theme, Mica, custom title bar, keyboard navigation and access keys, automation names
- ✅ Verified by launching the published build and capturing the Dashboard, Applications, Settings and Statistics pages in dark and light themes
- 🔲 Manual: tray menu clicks, notification appearance, Narrator, high DPI, narrow widths

Acceptance: users can configure and operate the complete workflow without editing configuration files. **Met**; interactive click-through is on the manual checklist.

## Phase 5: Packaging and reliability ✅ / 🔲

- ✅ Startup: HKLM Run entry from the installer, HKCU for portable runs, honours "Start with Windows"
- ✅ Crash recovery: `RegisterApplicationRestart`, dashboard restarts the agent, state restored from SQLite
- ✅ Self-contained publish (verified to run with no .NET installed on the PATH), portable zip
- ✅ WiX MSI: per-machine, Start menu shortcut, Run entry, optional strict-service feature, closes running instances, AppLocker rollback on uninstall. Builds with ICE validation (targeted suppressions documented in the `.wixproj`).
- ✅ GitHub Actions: Linux portable build + tests; Windows build, unit + integration tests, publish, MSI artifacts; tag-triggered draft release with checksums
- ✅ Recovery and limitations documented
- 🔲 Install → sign out/in → uninstall on a clean VM. The MSI was **not** installed on the development machine: that changes system state and needs your approval.
- 🔲 The workflows have not run on GitHub yet (no remote configured).

## Phase 6: Optional strict enforcement 🧪 / 🔲

- ✅ Compatibility detection (edition, AppIDSvc); correctly unsupported on this Windows 11 Home machine
- 🧪 Policy ownership and merging (`AppLockerPolicyDocument`): 8 unit tests
- ✅ Elevated service with an ACL'd pipe, caller verification (installed agent path) and user identity from impersonation
- ✅ Administrator consent CLI, rollback CLI, uninstall rollback, backups
- 🔲 Applying policies on Windows Enterprise/Education: not verifiable here. Test on a VM before relying on it.

The MVP does not depend on strict mode.

## Remaining work and ideas

- Code-sign the binaries and the MSI. Unsigned builds trigger SmartScreen, and signing lets strict mode use publisher rules.
- ARM64 builds: the projects and `Publish.ps1 -Architecture arm64` are wired up but untested.
- Optional Start-menu-shortcut scanning (`.lnk`) to find more installed apps.
- Localization; all strings are currently English.
- Choose and add a license. `installer/WorkToUnlock.Installer/License.rtf` is a neutral placeholder notice.
