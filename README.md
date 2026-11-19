# WU Health Guard

A Windows tray app that checks Windows Update health **before you restart**, warns you when a restart is
likely to fail, and fixes what it safely can.

## Why it exists

Windows offers "Update and restart" even when its servicing stack is half broken, and it never checks first.
A restart in that state can leave the PC in a boot loop or an endless "Undoing changes". The warning signs are
usually readable well before the restart: packages stuck pending for days, leftover reboot flags, a component
store DISM can't repair. Nothing in Windows looks at them. This app does, and shows the result as a tray light.

## The tray light

| Light | Meaning |
|-------|---------|
| 🟢 Green | Safe to restart. Either nothing is pending, or updates are staged normally and will finish during the restart. |
| 🟡 Yellow | Caution. Something should be fixed first: updates waiting days for a real restart, failed updates, a repairable component store, a stale reboot flag. |
| 🔴 Red | Don't restart. Servicing is running right now, or update packages are stuck in a way that can make the next restart fail or loop. |

Click the icon to open the report. Each finding explains the problem in plain language, says how long it has
been going on, shows the evidence (package names, registry keys, error codes), and lists the fixes that apply.

## What it checks

- **Servicing packages (CBS).** Packages in *Install Pending* or *Uninstall Pending*, and for how long.
  Pending since before the last restart means a restart already failed to finish them: they're stuck (red).
  Pending for days without any real restart is a warning (yellow).
- **pending.xml.** The list of servicing work Windows runs at the next boot. Red if it's older than the last
  restart, or if it's missing while packages are still pending. Leftovers like `pending.xml.bad` or
  `pending.old` are reported too.
- **Restart flags.** RebootPending, RebootRequired, PendingFileRenameOperations and related flags, including
  flags that survived a restart when they shouldn't have.
- **Component store.** DISM's health check, plus a full DISM scan on demand.
- **Update history.** Updates whose last install attempt failed, and updates that keep failing.
- **Fast Startup.** With Fast Startup on, *Shut down* is really a hibernate, so pending update work never runs.
  Only *Restart* finishes it.
- **Servicing in progress.** Whether Windows is installing something right now.

## What it does

- **Warns before a restart or shutdown.** While the light is red, Windows shows "WU Health Guard is preventing
  restart" with the reason, and you choose whether to go ahead.
- **Pauses Windows Update** automatically while the light is red (optional, off by default), so Windows can't
  stage another update on top of a broken state.
- **Notifies you** when the light gets worse.
- **Offers fixes** for each finding:

| Fix | Type |
|-----|------|
| Pause Windows Update | Safe: one click |
| Turn off Fast Startup | Safe: one click |
| Clear the Windows Update download cache (renamed, not deleted; refused while the light is red) | Safe: one click |
| Restart the Windows Update services | Safe: one click |
| Check system files (`sfc /scannow`) | Safe: one click |
| Repair the component store (`DISM /RestoreHealth`) | Risky: asks first, makes a backup |
| Clear a stale restart flag (only when no packages are stuck) | Risky: asks first, makes a backup |
| Remove a stuck, non-permanent update package | Risky: asks first, makes a backup |

Risky means anything that touches CBS, WinSxS or pending servicing state. Before a risky fix runs, the app
creates a System Restore point and exports the registry keys it will change. If any backup fails, nothing is
changed. Every action is logged to `C:\ProgramData\WuHealthGuard\logs`.

### What it won't do

- **Delete or rename pending.xml.** It can get a boot-looping PC to boot, but it leaves the packages stuck
  halfway, and the next restart can loop again.
- **Edit or delete CBS package records.** No backup makes that safe.
- **Guess at repairs it can't make.** When DISM can't find a good repair source, the app says that an
  in-place repair install is the fix: run `setup.exe` from a Windows ISO of the same version and keep files and apps.

## Install

Download `WuHealthGuardSetup.exe` from the [latest release](https://github.com/HitsuSan/wu-health-guard/releases/latest)
and run it. It:

- installs the .NET 10 Desktop Runtime if the PC doesn't have it yet (downloaded from Microsoft, and only run
  if it carries Microsoft's signature),
- installs the app to `C:\Program Files\WU Health Guard`,
- starts it at every logon without an administrator prompt.

The installer isn't code-signed yet, so Windows SmartScreen asks first: **More info → Run anyway**.

To remove it, uninstall **WU Health Guard** from Settings → Apps. Logs and backups in
`C:\ProgramData\WuHealthGuard` are kept; delete that folder too if you don't need them.

The release also has the bare `WuHealthGuard.exe` (about 2 MB) for people who already have the .NET 10
Desktop Runtime; run `WuHealthGuard.exe --install` from an administrator prompt to start it at logon.

## Updates

The app keeps itself up to date. Every 6 hours (or from **Check for updates** in the tray menu) it looks at
the latest release. It installs a new version only if the release's signature matches the key built into the
app and the download matches the signed hash. It never updates in the middle of a check or a fix, and if a new
version fails to start it goes back to the old one. You can turn this off with **Install updates
automatically** in the tray menu.

## Requirements and limits

- Windows 10 (1809 or later) or 11, x64.
- The .NET 10 Desktop Runtime (the installer sets it up).
- Administrator rights (reading servicing state and running DISM need them).
- English only for now.
- One copy runs per PC, started for the user who installed it.
- It can't repair a component store that has no good repair source; it tells you an in-place repair install
  is needed instead.

## Development

### Build

Requires the .NET 10 SDK. The core library and tests build and run on any OS; the Windows projects build on
Linux/macOS too (`EnableWindowsTargeting`), but the app only runs on Windows.

```bash
dotnet build WuHealthGuard.slnx
```

```bash
dotnet test WuHealthGuard.slnx
```

### Layout

| Project | What it holds |
|---------|---------------|
| `src/WuHealthGuard.Core` | Checks, verdict, auto-pause policy, fix runner, fixes, update signing. No Windows APIs. |
| `src/WuHealthGuard.Windows` | Registry, DISM API, Windows Update Agent, services, backups, update pause, update download. |
| `src/WuHealthGuard.Tray` | The WinForms tray app. |
| `tests/WuHealthGuard.Core.Tests` | Unit tests for the core, with in-memory fakes. |
| `tools/WuHealthGuard.ReleaseTool` | Signs release manifests in CI. |
| `installer/` | Inno Setup script for `WuHealthGuardSetup.exe`. |

Design notes and the test plan are in [docs/PLAN.md](docs/PLAN.md).

### Releases

Releases are signed with a key stored as the `UPDATE_SIGNING_KEY` repository secret. To create a key, run
`WuHealthGuard.exe --generate-update-key`; it saves a key file to the desktop. Put the private key in the
secret (Settings → Secrets and variables → Actions), then delete the file.

To publish: **Actions → Release → Run workflow**, keep the branch on `main`, and enter a version higher than
the last one. The workflow tests, builds, signs, tags and publishes `WuHealthGuardSetup.exe`,
`WuHealthGuard.exe` and `update.json`. The exe is framework-dependent (about 2 MB); `update.json` records
which .NET major version it needs, and installed copies won't take an update whose runtime is missing.
Developer and CI builds (`0.1.0-dev`) never update themselves.
