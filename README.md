# wu-health-guard

A Windows tray app that checks Windows Update health before you restart, warns you when a restart is likely to fail, and fixes what it safely can.

## Why it exists

Windows offers "Update and restart" even when its servicing stack is half broken, and it never checks first. A restart in that state can leave the PC in a boot loop. The warning signs are usually readable well before the restart: packages stuck pending for days, leftover reboot flags, a component store DISM can't repair. Nothing in Windows looks at them. This app does, and shows the result as a tray light.

## What happened on CHROMAZERO (Windows 11 26H2, build 26300, November 2026)

1. **Nov 9.** An update (RollupFix 26100.9818) half installed. Its packages stayed in Install Pending, and the old 9711 packages stayed in Uninstall Pending.
2. **Nov 16.** The PC boot-looped. Renaming `C:\Windows\WinSxS\pending.xml` got Windows to boot, but left those package records stuck halfway.
3. **Nov 17.** `DISM /RestoreHealth` failed with 3017 / 0x80070BC9, because the PC had only been shut down. Fast Startup is a hybrid hibernate, so pending servicing never ran. After a real restart, DISM reached 85% and failed with 0x800F0915 (repair source missing). CBS.log listed 8 corrupt WinSxS payloads, all version 10.0.26100.9818, including NetSetupEngine.dll, MMDevAPI.dll (x64 and wow64) and MicrosoftAccountWAMExtension.dll. Pausing and resuming Windows Update changed nothing.
4. **Nov 18.** After a Windows Update cache reset (SoftwareDistribution and catroot2), KB5132061 downloaded and staged with no logged error, so the restart looked safe. Nobody checked the stale pending packages first, and the restart boot-looped again.
5. **In WinRE.** There was no pending.xml, only pending.old. "Uninstall latest quality update" failed. `dism /remove-package` on the 9818 rollup returned 0x800f0825 (permanent package), and `/revertpendingactions` returned error 1168. Only System Restore brought the PC back, to build 26300.9711.
6. **After the restore.** The RebootPending and RebootRequired keys are still set, CBS packages are still in odd states, and Windows still offers "Update and shut down", so any normal restart could start the loop again. Updates are paused until Dec 16, 2026, and an in-place repair install is planned.

One look at the pending-package list before the Nov 18 restart would have prevented the second boot loop.

## What it checks

- CBS packages in Install Pending or Uninstall Pending, and how long they have been stuck.
- Whether `pending.xml` exists, and whether it is missing while packages are still pending.
- RebootPending, RebootRequired and PendingFileRenameOperations.
- `DISM /CheckHealth`, plus `/ScanHealth` on demand.
- Failed entries in the update history.
- Whether Fast Startup is on.

## What it does

- Shows a tray light: green when a restart is safe, yellow for caution, red for don't restart.
- Warns before a restart or shutdown, and can pause Windows Update automatically while the light is red.
- Applies safe fixes in one click, such as pausing updates, clearing the download cache or turning off Fast Startup.
- Applies risky fixes only after you confirm, and always backs up and logs first. Risky means anything that touches CBS, WinSxS or pending servicing state.

## Why build a new one

We looked at free tools first, and none covers the whole job:

- **WURepair** (SysAdminDoc, MIT) repairs after a failure. It has no stale-package check and no tray, and its fixes delete pending.xml and pending keys, which is unsafe as a one-click action.
- **isubroto/wum** is a command-line tool to pause and diagnose.
- **Windows Tech Suite Ultimate** has a tray icon, but when a restart is pending it just tells you to restart.
- **PendingReboot** (bcwilhite) only reads reboot flags.
- We also reviewed PSWindowsUpdate, WuMgr, WUMT, Reset Windows Update Tool, SetupDiag and DISM++.

We'll borrow ideas from them rather than adopt one.

## Limits

The app can't repair a component store that has no good repair source. When it reaches that point, it says an in-place repair install is the fix instead of guessing.

## Status

The full spec comes after CHROMAZERO is repaired.

## Development

### Build

Requires the .NET 10 SDK. The core library and tests build and run on any OS; the Windows projects
build on Linux/macOS too (`EnableWindowsTargeting`), but the app only runs on Windows 10/11.

```bash
dotnet build WuHealthGuard.slnx
```

```bash
dotnet test WuHealthGuard.slnx
```

```bash
dotnet publish src/WuHealthGuard.Tray -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

### Run

The app needs administrator rights (DISM API, servicing registry keys, fixes). To start it at logon
without a UAC prompt each time, register the scheduled task once from an elevated prompt:

```bash
WuHealthGuard.exe --install
```

`--uninstall` removes it. Logs and backups go to `%ProgramData%\WuHealthGuard`.

### Releases and updates

Installed copies update themselves. Every 6 hours (and from **Check for updates** in the tray menu) the app
downloads `update.json` from the latest GitHub release. It installs the new version only if:

- the manifest's signature verifies against the public key built into the app,
- the version is newer than the running one, and
- the downloaded exe matches the signed SHA-256.

Then it swaps the exe in place and restarts itself. If the new version doesn't start within a minute, it puts
the old one back. Updates never install in the middle of a scan or a fix. Turn them off with **Install updates
automatically** in the menu.

Developer and CI builds (`0.1.0-dev`) have no key built in and never update themselves.

**One-time setup** (creates the key that signs releases):

1. Run `WuHealthGuard.exe --generate-update-key`. It saves a key file to your desktop.
2. In GitHub: Settings > Secrets and variables > Actions > New repository secret, named `UPDATE_SIGNING_KEY`,
   with the private key from the file. Then delete the file.

**Publishing a release:** in GitHub, open Actions > Release > Run workflow, keep the branch on `main`, enter
the new version (e.g. `0.2.1`, higher than the last one) and run it. The workflow tests, builds, signs,
tags and publishes `WuHealthGuard.exe` and `update.json`. Pushing a `vX.Y.Z` tag does the same.

Install the first release by hand (download `WuHealthGuard.exe` from the release); it keeps itself up to date
after that.

### Layout

| Project | What it holds |
|---------|---------------|
| `src/WuHealthGuard.Core` | Checks, verdict, auto-pause policy, fix runner and fixes. No Windows APIs. |
| `src/WuHealthGuard.Windows` | Registry, DISM API, Windows Update Agent, services, backups, update pause. |
| `src/WuHealthGuard.Tray` | The WinForms tray app. |
| `tests/WuHealthGuard.Core.Tests` | Unit tests for the core, with in-memory fakes. |
| `tools/WuHealthGuard.ReleaseTool` | Signs release manifests in CI. |

The plan and phase status are in [docs/PLAN.md](docs/PLAN.md).
