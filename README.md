# Work to Unlock

Earn time in distracting apps by actively working in approved ones.

Work 60 minutes in Visual Studio Code or Blockbench, earn 20 minutes with Steam and Discord. When the reward runs out, Steam and Discord are locked again. Repeat.

![Dashboard](docs/images/dashboard.png)

## How it works

| Piece | What it does |
|---|---|
| **Agent** (`WorkToUnlock.Agent.exe`) | Per-user background process, started at sign-in, no admin rights. Samples the foreground app every 500 ms, detects idle time, lock/unlock, Remote Desktop disconnects and sleep, accumulates qualifying work, runs the reward state machine, persists everything to SQLite, enforces restrictions, and owns the tray icon. |
| **Dashboard** (`WorkToUnlock.App.exe`) | WinUI 3 window (Fluent, Mica, light/dark). A client of the agent over a per-user named pipe. Closing it never stops tracking or enforcement. |
| **Strict service** (`WorkToUnlock.Service.exe`, optional) | Elevated Windows service that can block restricted apps from *starting* using AppLocker rules it owns. Off unless an administrator enables it. |

Reward states: **Working** → (work target reached) → **Reward ready** → (you click *Unlock*) → **Unlocked** → (reward time elapsed) → back to **Working** with progress reset.

Only time that is genuinely in the foreground of an approved app, while you are active, with the session unlocked and the PC awake, counts. A pause without keyboard or mouse input (reading, thinking) is added when you continue within the idle threshold (default 60 s); if you step away for longer, none of that pause counts. Work to Unlock cannot tell whether the work is meaningful, and it never reads window titles, documents, keystrokes or screenshots.

## Features

- **Work and break presets**: one click for 25/5, 50/10, 60/20 or 90/30, or set your own.
- **Two ways to earn breaks**: a full break each time you reach the work requirement, or *earn as you go*, where work saves break time in the same proportion (for example 20 minutes per hour) up to a limit. Spend saved time whenever you like and end a break early to keep the rest.
- **Pause breaks while away** (optional): a running break stops counting down while the PC is locked or asleep.
- **Profiles**: save setups such as *School* (60/10, every game blocked) and *Weekend* (25/15, Discord allowed) and switch between them from the dashboard.
- **Daily goal and streaks**: a daily work goal, the current and best streak, and a 14-day chart.
- **Achievements**: twelve milestones for total work, focused days, streaks, rewards and a clean week.
- **Blocked attempts**: how often you tried to open a locked app today, per app.
- **Honesty report**: the last 7 days of time when locked apps were not being blocked, because Work to Unlock was closed or ended from Task Manager, or enforcement was turned off. Signing out and shutting down don't count.
- **Updates**: a daily check of this repository's releases, and *Update now* to install a newer version.

## Install

1. Download `WorkToUnlock-<version>-x64.msi` from the [latest release](https://github.com/atcpybd/work-to-unlock/releases/latest) (or build it, see below).
2. Run it. The installer needs administrator approval once, because it installs to Program Files and registers the agent to start at sign-in for every user.
3. Leave **Strict enforcement service** unselected unless you run Windows Enterprise, Education or Server and want launch blocking (see [docs/enforcement.md](docs/enforcement.md)).
4. Tick **Open Work to Unlock** on the last page. On first start the agent adds Visual Studio Code and Blockbench as work apps and Steam and Discord as restricted apps if they are installed.

Requirements: Windows 10 version 2004 (build 19041) or later, or Windows 11, x64. No separate .NET or Windows App SDK runtime is needed; the build is self-contained.

A portable build (`WorkToUnlock-<version>-win-x64.zip`) is also produced. Unzip it anywhere and run `WorkToUnlock.App.exe`; the agent registers itself for the current user only.

**Uninstall** from *Settings > Apps*. Uninstalling closes the agent and dashboard, removes the startup entry, and removes any AppLocker rules the strict service created. Your local data in `%LOCALAPPDATA%\WorkToUnlock` is kept; delete that folder to remove it.

## Build from source

Requirements: .NET 10 SDK. The WinUI dashboard and the installer build on Windows only; everything else (including all unit tests) also builds on Linux.

```bash
dotnet build WorkToUnlock.sln
```

```bash
dotnet test tests/WorkToUnlock.Core.Tests
```

```bash
dotnet test tests/WorkToUnlock.Agent.Tests
```

```bash
dotnet test tests/WorkToUnlock.IntegrationTests
```

```bash
pwsh ./build/Publish.ps1 -Version 1.1.0
```

`Publish.ps1` writes a self-contained app folder to `artifacts/publish/win-x64`, a portable zip to `artifacts/`, and the MSI to `artifacts/installer/`. On Linux, use `WorkToUnlock.Portable.slnf`.

Keep the repository path short (under about 60 characters): the WinUI XAML compiler fails on paths longer than 260 characters even with long paths enabled.

## Repository layout

```text
src/
  WorkToUnlock.Core/            Domain: reward state machine, work tracker, rule matching, safety rules, IPC contracts
  WorkToUnlock.Infrastructure/  SQLite persistence and migrations, named pipes with ACLs, Win32 tracking interop, logging
  WorkToUnlock.Enforcement/     Process enforcement, application discovery, AppLocker policy management
  WorkToUnlock.Agent/           Background agent: hosting, tray, session/power notifications, IPC server
  WorkToUnlock.App/             WinUI 3 dashboard (MVVM, CommunityToolkit.Mvvm)
  WorkToUnlock.Service/         Optional elevated strict-enforcement service
tests/
  WorkToUnlock.Core.Tests/        Reward engine, tracker, matcher and validation (portable)
  WorkToUnlock.Agent.Tests/       Agent services, process enforcer, SQLite, AppLocker merging (portable)
  WorkToUnlock.IntegrationTests/  Native Windows tests and an end-to-end agent test
  WorkToUnlock.TestTarget/        Harmless stand-in "restricted app" used by the integration tests
installer/WorkToUnlock.Installer/ WiX v6 MSI
build/Publish.ps1                 Publish + package
tools/                            Icon generator, agent request helper, window capture
docs/                             Architecture, enforcement, testing, recovery, implementation plan
```

## Documentation

- [Architecture](docs/architecture.md)
- [Enforcement](docs/enforcement.md): what is and is not guaranteed
- [Testing](docs/testing.md): automated coverage and the manual Windows test checklist
- [Recovery](docs/recovery.md): crashes, data corruption, AppLocker rollback, uninstall
- [Implementation plan and status](docs/implementation-plan.md)

## Privacy

Everything stays on your PC. The agent stores, per approved app, when qualifying work started and ended and how many milliseconds counted, plus reward history and an activity log of setting changes. It does not store window titles, document names, URLs, keystrokes or screenshots. The only network access is the update check: once a day (and when you choose *Check now*) the agent reads the latest release of [atcpybd/work-to-unlock](https://github.com/atcpybd/work-to-unlock/releases) from the GitHub API, sending nothing about you or your activity. Turn it off in *Settings > Updates*. *Update now* downloads the installer from that release, checks it against the release's `SHA256SUMS.txt`, and runs it; Windows asks for permission first. *Settings > Export local data* writes everything it has to a JSON file.

## Known limitations

- Process enforcement is best effort: a restricted app can run for up to about one second before it is closed, and the agent can be ended by the user from Task Manager. Strict mode (AppLocker) closes that gap on supported editions only.
- Strict mode is unavailable on Windows Home and Pro, which do not enforce local AppLocker policies.
- A restricted app started with administrator rights cannot be closed by the agent; the dashboard reports this as *Partly protected*.
- Notifications are tray balloon notifications from the agent; they respect Focus Assist / Do Not Disturb.
- The honesty report can't see time before a restart of Windows: if the agent is ended and the PC is then restarted, that gap isn't reported. Allowing a restricted app, by turning it off on the Applications page or switching to a profile that allows it, isn't counted either (profile switches do appear in the activity log).
- Achievements and streaks use the stored history, which data retention limits (90 days by default); achievements stay unlocked once earned.

See [docs/implementation-plan.md](docs/implementation-plan.md) for what was verified on real Windows and what still needs manual testing.
