# WU Health Guard — Plan

The [README](../README.md) is the spec: what the app checks, what it does, and the CHROMAZERO incident it
has to prevent. This file says how it's built and where each requirement lives.

## 1. The tray light

| Light | Verdict | Meaning |
|-------|---------|---------|
| Green | Safe to restart | Nothing pending, or updates staged normally that will finish during the restart. |
| Yellow | Caution | Fix first: updates pending for days without a real restart (Fast Startup), failed updates, a repairable component store, a stale reboot flag. |
| Red | Don't restart | Packages stuck across a restart, pending.xml stale or missing while packages are pending, servicing running right now, or a component store that can't be repaired. |

Clicking it opens a report: each finding has a plain-language explanation, how long the problem has
existed, the raw evidence (package names, registry keys, HRESULTs) and buttons for the fixes that apply.

## 2. Requirement coverage

Every README item and incident step, where it's implemented and which test proves it.
Tests are in `tests/WuHealthGuard.Core.Tests`; `ChromazeroScenarioTests` replays the incident through the
full scanner.

### What it checks

| README | Implementation | Tests |
|--------|----------------|-------|
| CBS packages in Install/Uninstall Pending, **and how long** | `CbsPendingPackagesCheck`: grouped per state, with age ("stuck in Install Pending for 9 days"). Pending since before the last boot = stuck (red). Pending for more than 3 days with no real restart = yellow. Resolving/Staging after a restart = yellow. | `CbsPendingPackagesCheckTests` |
| `pending.xml` exists; **missing while packages are pending** | `PendingXmlCheck`: fresh = green, older than last boot = red, missing while packages are pending (after a 1-hour install grace) = red. Leftovers (`pending.old`, `pending.xml.bad`, …) listed. | `PendingXmlCheckTests` |
| RebootPending, RebootRequired, PendingFileRenameOperations | `RebootFlagsCheck` (also RebootInProgress, PackagesPending, Services\Pending, UpdateExeVolatile). Flags older than the last boot = stale (yellow). | `RebootFlagsCheckTests` |
| `DISM /CheckHealth`, `/ScanHealth` on demand | `ComponentStoreCheck` over the DISM API (`DismCheckImageHealth`, language-independent). "Full component store scan" button runs ScanHealth. | `ComponentStoreCheckTests` |
| Failed update history | `FailedUpdatesCheck` (WUA COM): latest attempt failed and not later succeeded; 3+ failures escalate. | `FailedUpdatesCheckTests` |
| Fast Startup on | `FastStartupCheck`: info normally; yellow when packages have waited 3+ days, because Shut down never finishes them. | `FastStartupCheckTests` |
| (implied) servicing running now | `ServicingActivityCheck`: TiWorker.exe running = red. | `ServicingActivityCheckTests` |

### What it does

| README | Implementation | Tests |
|--------|----------------|-------|
| Tray light green / yellow / red | `TrayIcons`, `HealthReport.Verdict`. | `HealthReportTests` |
| Warns before a restart or shutdown | `ShutdownGuardWindow`: while red, answers `WM_QUERYENDSESSION` with a block reason so Windows shows "WU Health Guard: …" with Cancel / Restart anyway. **On by default.** Plus a balloon whenever the light gets worse. | VM test (§5) |
| Pauses Windows Update automatically while red | `GuardPolicy`: opt-in, triggers at red. If a pause runs out while still red, the next scan pauses again; an existing pause is never extended. | `GuardPolicyTests`, scenario Nov 18 |
| Safe fixes in one click: pause updates, clear download cache, turn off Fast Startup | `PauseUpdatesFix`, `ResetUpdateCacheFix` (renames, never deletes; **refused while red**, see Nov 18), `TurnOffFastStartupFix`; also restart WU services, `sfc /scannow`. | `FixRunnerTests` |
| Risky fixes (anything touching CBS, WinSxS or pending state) only after confirmation, with backup and log | `FixRunner`: confirm → back up (restore point + `reg export`) → apply → log. A failed backup stops the fix. Risky: DISM RestoreHealth, clear stale reboot flag, remove a stuck package. | `FixRunnerTests` |
| Limit: no repair source → say in-place repair install | `DismErrors` maps 0x800F081F / 0x800F0915 / 0x800F0906 to "an in-place repair install is the fix"; "not repairable" finding says the same. | `Dism_failures_are_explained`, scenario Nov 17 |

### The CHROMAZERO incident, step by step

| Step | What the app shows / does | Test |
|------|---------------------------|------|
| Nov 9–15: rollup half installed, PC only shut down | Yellow: "3 update packages waiting 5 days for a restart" + "Fast Startup is keeping updates from finishing"; offers Turn off Fast Startup. | `Nov15_…` |
| Nov 16: pending.xml renamed to escape the loop | Red: packages stuck in Install/Uninstall Pending, pending.xml missing, pending.old leftover. Offers Pause updates. Never offers renaming pending.xml or removing the permanent rollup. | `Nov16_…` |
| Nov 17: DISM 3017, then 0x800F0915 | Failure explained: "Use Restart, not Shut down" / "in-place repair install is the fix". | `Nov17_…` (both) |
| Nov 18: cache reset, KB5132061 staged cleanly | **Still red**, because the old packages are stuck. The restart is held, auto-pause kicks in, and clearing the cache is refused while red. This is the restart that would have been prevented. | `Nov18_…` (both) |
| WinRE: remove-package 0x800F0825 | Remove-package is never offered for RollupFix / servicing stack packages; the error is explained if it happens. | `Remove_package_is_not_offered_for_permanent_rollups` |
| After System Restore: flags set, packages odd, paused to Dec 16 | Red. "Clear stale restart flag" is not offered (it would hide the stuck packages); "Pause" is not offered (already paused); the existing pause isn't changed. | `After_system_restore_…` |

### Deliberately not done

- **No pending.xml delete/rename fix.** It's what got CHROMAZERO booting on Nov 16, and also what left the
  packages stuck halfway for the Nov 18 loop. A booted PC never needs it; the app recommends System Restore or
  an in-place repair install instead.
- **No deleting CBS package keys or editing their states.** No backup makes that safe.
- **No automatic DISM repair.** It writes to WinSxS, so it's a confirmed, backed-up fix.

## 3. Architecture

```
src/
  WuHealthGuard.Core       net10.0           Checks, verdict, guard policy, fix runner, fixes. No Windows APIs.
  WuHealthGuard.Windows    net10.0-windows   Registry (incl. key last-write times), DISM API, WUA COM,
                                             services, processes, restore points, reg export, update pause,
                                             JSONL action log.
  WuHealthGuard.Tray       net10.0-windows   WinForms tray app, report window, shutdown guard, logon task.
tests/
  WuHealthGuard.Core.Tests xunit with in-memory fakes; runs on any OS.
```

**"Before the last boot"** is the core signal. Boot time is the kernel boot (tick count). With Fast Startup a
Shut down doesn't change it, which is correct: only a real restart processes pending servicing.

**Elevation**: the app runs elevated, started at logon by a "run with highest privileges" scheduled task
(`--install`), so there's no UAC prompt each logon.

**Data**: `%ProgramData%\WuHealthGuard\` holds `settings.json`, `logs\actions-yyyy-MM.jsonl` and `backups\`.

### Auto-update

| Piece | Implementation | Tests |
|-------|----------------|-------|
| Signed manifest | `update.json` = version, HTTPS URL, SHA-256, ECDSA P-256 signature over all three (`UpdateSignature`). The private key exists only as the `UPDATE_SIGNING_KEY` Actions secret; release builds embed the public key. | `UpdateTests` |
| Check | Every 6 h + menu. Rejects bad signatures, non-HTTPS URLs, malformed hashes, same or older versions (no downgrade replay). | `UpdateTests` |
| Download | Into `%ProgramData%\WuHealthGuard\updates`, which the app locks to Administrators/SYSTEM (owner: Administrators), so a standard user can't swap the file before the elevated app runs it. Size cap, hash checked before and right before install. | VM test |
| Install | Rename running exe to `.old`, copy new exe in, hand over the single-instance slot, start it, wait up to 60 s for it to signal that its tray icon is up; otherwise roll back. | VM test |
| Release | `.github/workflows/release.yml` on `v*.*.*` tags: test, derive public key, publish single-file exe, sign manifest, create GitHub Release. | First tag |

## 4. Phases

1. **Core** — checks, verdict, guard policy, fix runner, fixes, unit + incident scenario tests. ✅ done
2. **Windows adapters** — 🟡 written and compiling; not yet run on Windows.
3. **Tray app** — 🟡 written and compiling; not yet run on Windows. Still to do: settings window, scan on
   WU state change, proper icon assets.
4. **VM validation** — §5.
5. **Packaging** — single-file publish, signing, screenshots.

## 5. Validation on real Windows

Unit tests cover the decisions. The OS-facing parts need a VM (Windows 11 24H2/25H2 and the 26H2 build
CHROMAZERO runs, plus Windows 10 22H2), with snapshots:

- Fresh cumulative update installed, not restarted → green ("will finish during restart").
- Fast Startup on, update staged, shut down/power on for 4 days → yellow, Fast Startup warning.
- Set a package's `CurrentState` to `0x60` and restart → red, stuck.
- Rename pending.xml with packages pending (snapshot first!) → red, missing.
- Start a restart while red → the shutdown block screen shows the reason.
- Auto-pause on + red → Settings > Windows Update shows paused.
- Each fix end to end, checking the log and the backup folder.

Values to confirm on real machines: CBS `CurrentState` codes (`0x60`, `0x05`, `0x10`, `0x30`), that the
UX\Settings pause values are honoured, whether Administrators can delete CBS reboot flag keys, and that a
tray-only app's shutdown block reason is shown.

## 6. Open questions

- Auto-pause is opt-in. Should it be on by default, given the incident?
- Restore points are required for risky fixes; that blocks them when System Protection is off.
- English only for now. Detection doesn't parse localized tool output (registry and APIs only).
