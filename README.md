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
