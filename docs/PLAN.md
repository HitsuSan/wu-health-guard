# WU Health Guard — Plan

> Windows tray app that checks Windows Update health **before you restart**: flags stuck CBS
> Install/Uninstall Pending packages, `pending.xml` and reboot flags, DISM component-store damage and
> failed updates, warns or auto-pauses updates, and offers safe one-click fixes (risky ones need
> confirmation, with backup and log).

## 1. What the user gets

A tray icon that answers one question: **"Is it safe to restart right now?"**

| Icon | Verdict | Meaning |
|------|---------|---------|
| Green | Safe to restart | Nothing pending, nothing broken. |
| Blue | Restart recommended | Updates are staged normally and will finish at the next restart. |
| Yellow | Caution | Something is off (failed updates, a repairable component store, a stale reboot flag). Restart is probably fine, but fix this first. |
| Red | Don't restart yet | Servicing is mid-flight, or the state looks stuck or damaged in a way that can cause a failed boot or an "Undoing changes" loop. |

Clicking the icon opens a report: each finding has a plain-language explanation, the raw evidence
(registry key, package name, HRESULT…), and the fixes that apply to it.

## 2. Detection (checks)

Each check is a small class over a mockable abstraction, so the logic is unit-tested on any OS.

| Check | Source | Normal | Problem |
|-------|--------|--------|---------|
| **CBS pending packages** | `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\Packages\*\CurrentState` (`0x60` Install Pending, `0x05` Uninstall Pending) | Pending, written **after** the last boot → finishes at next restart | Pending, last written **before** the last boot → survived a restart → **stuck** |
| **Reboot flags** | CBS `RebootPending`, `RebootInProgress`, `PackagesPending`; WU `Auto Update\RebootRequired`, `Services\Pending`; `PendingFileRenameOperations`; `Updates\UpdateExeVolatile` | Set after last boot | Set before last boot → stale flag |
| **pending.xml** | `%windir%\WinSxS\pending.xml` (+ `pending.xml.bad`) | Newer than last boot | Older than last boot → servicing transaction that didn't complete at boot |
| **Servicing in progress** | `TiWorker.exe` running | — | Restarting now interrupts the servicing stack |
| **Component store** | DISM API `DismCheckImageHealth` (fast; ScanHealth on demand) | Healthy | Repairable / Not repairable |
| **Failed updates** | WUA COM `Microsoft.Update.Session` → `QueryHistory` | — | Latest attempt of an update failed/aborted; repeated failures |

The "stuck" heuristic relies on comparing timestamps (registry key last-write time, file time)
with the last boot time. That is the core idea and needs validation on real machines (§7).

## 3. Guard actions

- **Warn**: tray icon + toast when the verdict worsens.
- **Auto-pause updates** (opt-in, default off): when the verdict is *Caution* or worse, pause updates
  for N days (default 7) the same way the Settings app does (`HKLM\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings`
  `Pause*` values), so Windows doesn't force a restart onto a broken state. One-click resume.
- **Shutdown guard** (opt-in): on *Don't restart yet*, register a shutdown block reason
  (`ShutdownBlockReasonCreate`) so a user-initiated restart shows "WU Health Guard: servicing is in
  progress" and can be cancelled. Best-effort: Windows can override it.

## 4. Fixes

Every fix run is logged (JSON lines, `%ProgramData%\WuHealthGuard\logs`). **Risky** fixes also need
explicit confirmation and take a backup first (system restore point + `reg export` of touched keys + copies
of touched files, under `%ProgramData%\WuHealthGuard\backups\<timestamp>-<fix>`). If the backup fails,
the fix does not run.

| Fix | Risk | What it does |
|-----|------|--------------|
| Restart Windows Update services | Safe | Restart `wuauserv`, `bits`, `cryptsvc`. |
| Repair component store | Safe | `DISM /Online /Cleanup-Image /RestoreHealth`. |
| System file check | Safe | `sfc /scannow`. |
| Reset update download cache | Risky | Stop services, rename `SoftwareDistribution` and `catroot2` to `*.wuhg-<ts>`, restart services. |
| Clear stale reboot flags | Risky | `reg export`, then delete the stale flag keys. |
| Set aside stale pending.xml | Risky | Take ownership, rename to `pending.xml.wuhg-<ts>`. Shown with a strong warning; recommend `DISM /Image:C:\ /Cleanup-Image /RevertPendingActions` from WinRE as the alternative. |
| Remove stuck package | Risky | `DISM /Online /Remove-Package /PackageName:<name>`. |

## 5. Architecture

```
src/
  WuHealthGuard.Core       net10.0           Model, checks, verdict, guard policy, fix runner, fixes.
                                             No Windows APIs: everything goes through interfaces.
  WuHealthGuard.Windows    net10.0-windows   Real adapters: registry (incl. key last-write time),
                                             DISM API, WUA COM, services, processes, restore points,
                                             update pause, logging.
  WuHealthGuard.Tray       net10.0-windows   WinForms NotifyIcon app, report window, settings,
                                             scheduler, toasts, shutdown guard.
tests/
  WuHealthGuard.Core.Tests xunit, runs on Linux/Windows with fakes.
```

**Elevation**: the checks (DISM API, some registry) and all fixes need admin. v1 runs the tray app
elevated, started at logon by a scheduled task with "run with highest privileges" (no UAC prompt each
logon). A split "unelevated tray + elevated service" is a possible v2.

**Distribution**: self-contained single-file `win-x64` (and `win-arm64`) publish; `--install` /
`--uninstall` switches register/remove the logon task.

## 6. Phases

1. **Foundation** — plan, solution, CI (Windows runner), core model, all checks against fakes,
   verdict + auto-pause policy, fix runner (confirm → backup → apply → log), unit tests. ✅ *done*
2. **Windows adapters** — registry reader with `RegQueryInfoKey` last-write times, boot time, DISM API
   probe, WUA history, process/service control, restore points, `reg export`, pause controller.
   🟡 *written and compiling; not yet run on Windows*
3. **Tray app** — icon states, menu, report window with per-finding fixes, settings (JSON), periodic
   scan (hourly + on resume), balloon notifications, shutdown guard, `--install` logon task.
   🟡 *skeleton written and compiling; not yet run on Windows. Still to do: settings window, scan on
   WU state change, proper icon assets.*
4. **Fixes** — implement and test each fix end-to-end in a VM.
5. **Packaging** — single-file publish, install/uninstall, code signing, README with screenshots.

## 7. Testing on real Windows

Unit tests cover the decision logic. The OS-facing parts need a VM matrix (Windows 10 22H2,
Windows 11 24H2/25H2, x64 + one arm64) with snapshots, and scripted scenarios:

- Fresh LCU installed, not restarted → expect *Restart recommended*.
- Set a CBS package's `CurrentState` to `0x60` and reboot → expect *stuck* → red.
- Leave a dummy `pending.xml` across a reboot (snapshot first!) → red.
- Corrupt the component store flag (or use a known-bad image) → yellow/red.
- Force an update failure (e.g. block the download host) → yellow.

## 8. Open questions

- Elevation model (§5): elevated tray via scheduled task is the v1 default.
- Auto-pause default: off, opt-in. Pause length default 7 days.
- Which risky fixes ship in v1? `pending.xml` and package removal are the most dangerous; they could
  stay "guidance only" until VM-tested.
- Localization: v1 English only; detection doesn't parse localized tool output (uses registry and APIs).
