# In-place repair helper: research and plan

Status: **implemented, not yet tested on real hardware** (see §5).

When DISM can't repair the component store (0x800F081F / 0x800F0915 / 0x800F0906), the app used to tell the
user "run setup.exe from a Windows ISO of the same version and keep files and apps" and stop there. This turns
that sentence into a guided, confirmed fix that works on any PC the app supports.

Code: `src/WuHealthGuard.Core/Repair/` (rules, advice, Setup command lines and exit codes, the fix),
`src/WuHealthGuard.Windows/InstallMediaReader.cs` (ISO mount, DISM image list, PC identity, pre-checks),
`src/WuHealthGuard.Tray/RepairForm.cs` (the window). Tests: `tests/WuHealthGuard.Core.Tests/RepairTests.cs`.

## 1. Verified facts

Each fact is tagged with where it comes from. **MS** = Microsoft documentation or Microsoft's own download page.
**Community** = consistent reports from forums and guides, not documented by Microsoft. The design only relies
on Community facts where Setup itself checks them (see §3, step 4).

### Getting media

| # | Fact | Source |
|---|------|--------|
| M1 | Microsoft's public download page offers the **current** Windows 11 release as an ISO. On 2026-11-19 it shows "Windows 11 2026 Update, Version 26H2". The x64 ISO is multi-edition, with a language picker covering dozens of languages; Arm64 ISOs are on a separate page. The page doesn't print the exact build/UBR. | MS: [Download Windows 11](https://www.microsoft.com/software-download/windows11) |
| M2 | 24H2, 25H2 and 26H2 share one servicing branch. 26H2 reaches 24H2/25H2 PCs as a small **enablement package**, not a full OS upgrade. That's why the registry on a 26H2 PC still shows `BuildLab 26100…` and kernel `10.0.26100.x` next to `CurrentBuild 26300`. | MS: [What's new in 26H2](https://learn.microsoft.com/windows/whats-new/whats-new-windows-11-version-26h2) |
| M3 | Microsoft only offers the **current** version on that page. Older versions, Insider builds and LTSC aren't there: Insider ISOs come from the Insider download page (only some builds); LTSC/Enterprise from volume licensing or the Evaluation Center. | MS download page (what is and isn't listed) |

So there's nothing odd about needing "a specific ISO": for a normal, up-to-date Home/Pro PC it's simply the ISO
Microsoft is offering today, in the PC's language. It only gets hard for PCs that are *not* on the current
release (Insider, LTSC, older versions held back, Windows 10).

### What "keep files and apps" requires

| # | Fact | Source |
|---|------|--------|
| K1 | `setup /auto upgrade` "performs an upgrade of Windows saving apps and data. If the upgrade installation option is not available … Windows Setup will exit silently and log an exit code." It doesn't fall back to wiping anything. | MS: [Setup command-line options](https://learn.microsoft.com/windows-hardware/manufacture/desktop/windows-setup-command-line-options) |
| K2 | Since Windows 11 22H2, `/auto upgrade` requires media with the **same System Default UI Language** as the PC. Cross-language upgrades (even en-US → en-GB) are no longer allowed. | MS: same page |
| K3 | Changing to a lower edition keeps personal files but **removes apps and settings**. General Availability → LTSC in place isn't supported. | MS: [Windows upgrade paths](https://learn.microsoft.com/windows/deployment/upgrade/windows-upgrade-paths) |
| K4 | Architecture must match (x64 media for x64, Arm64 for Arm64). | Community (and self-evident) |
| K5 | Media with an **older build** than the PC (e.g. a 24H2/26100 ISO on a 26H2/26300 PC) won't keep apps: Setup treats it as a downgrade. | Community ([ElevenForum](https://www.elevenforum.com/t/repair-install-windows-11-with-an-in-place-upgrade.418/), [ElevenForum](https://www.elevenforum.com/t/cant-repair-install-with-dism-the-repair-content-could-not-be-found-anywhere.49088/)); not documented by MS |
| K6 | Media with the same build but an **older monthly update** (lower UBR, e.g. 26300.9550 on a 26300.9711 PC) is the normal repair-install case and keeps apps. | Community; not documented by MS |

### Setup's own pre-check (the key to doing this safely)

| # | Fact | Source |
|---|------|--------|
| S1 | `setup /auto upgrade /quiet /compat scanonly` runs Setup's compatibility checks and exits **without installing**. Exit codes: `0xC1900210` no issues; `0xC1900208` compatibility block (apps etc.); **`0xC1900204` the chosen migration ("keep apps") isn't available**; `0xC1900200` PC not eligible; `0xC190020E` not enough disk space. | MS: Setup command-line options |
| S2 | With `/quiet`, Setup needs `/eula accept` or it fails with `0xC190010E`. If several images match and `/quiet` is set, it fails with `0xC1900215`. | MS: same page |
| S3 | `/dynamicupdate disable` stops Setup fetching updates from Windows Update during the upgrade. | MS: same page |

S1 matters most: it lets **Setup itself** decide whether this media can keep apps on this PC, so the app doesn't
have to re-implement Microsoft's rules (K5 and K6 aren't documented, and could change).

### "Fix problems using Windows Update"

| # | Fact | Source |
|---|------|--------|
| F1 | Settings > System > Recovery > "Fix problems using Windows Update" reinstalls the **same version** already on the PC, repairs system files and components, and keeps apps, files and settings. | MS: [Fix issues by reinstalling the current version](https://support.microsoft.com/topic/497ac6da-7cac-4641-82a5-f50398d879a0) |
| F2 | Needs Windows 11 22H2 or later **with the February 2024 optional update or later**, internet and power throughout. May be missing on work/school-managed PCs, Autopatch, or with some Windows Update policies. **Not available on Windows 10.** | MS: same article |
| F2a | The February 2024 optional update is KB5034848: builds **22621.3235** (22H2) and **22631.3235** (23H2). 24H2 and later (26100+) shipped after it. | MS: [KB5034848](https://support.microsoft.com/kb/5034848) |
| F3 | Microsoft's article doesn't say whether paused updates block it. | MS: same article (silent) |
| F4 | `ms-settings:recovery` opens the Recovery page. I found no documented way for an app to *start* the reinstall itself. | MS: [Launch Settings](https://learn.microsoft.com/windows/apps/develop/launch/launch-settings) |

### Not verified

- Whether Microsoft still offers a **Windows 10 22H2** ISO in November 2026 (Windows 10 support ended October
  2025). Needs checking before the app promises anything for Windows 10.
- The exact registry source Setup uses for "System Default UI Language". `HKLM\SYSTEM\CurrentControlSet\Control\Nls\Language\InstallLanguage`
  is the usual one; confirm against `dism /online /get-intl` on test VMs.

## 2. Example: the incident PC (2026-11-19)

Windows 11 Pro, 26H2, build 26300.9711, en-US, x64, not Insider, BitLocker off, 270 GB free.

- ISO route: the current Microsoft ISO (26H2, English (United States), x64) fits M1/K2–K6. Setup's scan-only
  (S1) confirms before anything changes.
- Settings route: fits F1/F2 (Windows 11 26H2, unmanaged), but needs Windows Update unpaused (F3 unknown).

## 3. Design

A new **guided** fix, "In-place repair install", offered on findings where DISM reports a missing repair
source, and on stuck CBS packages. Steps 1–4 change nothing. Step 5 runs only after confirmation.

1. **Identify the PC** (registry only, no parsing of localized output): product, `DisplayVersion`,
   `CurrentBuild`, `UBR`, `EditionID`, install language, architecture, Insider state
   (`WindowsSelfHost\Applicability` `BranchName` / `EnablePreviewBuilds`), managed state if detectable.
2. **Pick a route** and explain it:
   - Windows 11 22H2+ and not managed → offer "Fix problems using Windows Update" first: a button opens
     `ms-settings:recovery` (F4), with a note if the app or the user paused updates.
   - Otherwise, or as the alternative → the ISO route, with the exact media to get: "Windows 11 26H2,
     English (United States), x64, from microsoft.com/software-download/windows11". The app links to the
     official page and **doesn't download** anything itself.
   - Insider, LTSC, older-than-current or Windows 10 → say plainly where media comes from, or that none may
     exist.
3. **Check the ISO** the user picks: mount it read-only, read `sources\install.wim` / `install.esd` with the
   DISM API the app already uses (`DismGetImageInfo`: edition, version, build, UBR, languages, architecture), and
   show a verdict per rule K2–K6 ("Pro, en-US, x64, 26300.9550: should keep apps").
4. **Pre-checks**, then **Setup's own scan**: free space, BitLocker (remind to have the recovery key), AC power,
   then `setup.exe /auto upgrade /quiet /compat scanonly /eula accept /dynamicupdate disable`. Map the exit code
   (S1) to plain language. Only `0xC1900210` unlocks step 5. `0xC1900204` → "this ISO can't keep your apps",
   with the likely reason from step 3.
5. **Run the repair** (Risky: confirmation + restore point, same as the other risky fixes):
   `setup.exe /auto upgrade /eula accept /dynamicupdate disable`, without `/quiet` so the user sees Setup's
   progress. Log the exit code and copy Setup's logs to `backups\`.

### Things to handle

- **The app's own shutdown guard.** While the light is red the app blocks restarts, and Setup restarts the PC.
  The guard must stand down while a repair it started is running.
- **Auto-pause.** The ISO route with `/dynamicupdate disable` doesn't need Windows Update, so a pause can stay.
- **Message update.** `DismErrors.InPlaceRepair` should say "same edition, same language, same or newer
  version" and point to the new fix instead of only to setup.exe.
- **Windows 10.** No "Fix problems using Windows Update" (F2); ISO availability unverified.

### Testing

- Core: unit tests for route choice and the ISO verdict (fakes for registry and image info), and for exit-code
  mapping.
- VM: 26H2 Pro en-US with a matching ISO (expect `0xC1900210`), a 25H2 ISO (expect `0xC1900204` or similar), a
  different-language ISO, an Enterprise eval ISO; Windows 10 22H2.

## 4. Unrelated bug spotted

The self-updater replaces `WuHealthGuard.exe` but doesn't update the uninstall entry: the incident PC runs 0.2.6 while
Settings > Apps still shows 0.2.3.

## 5. What's verified and what isn't

Done:

- Unit tests (`RepairTests.cs`) for the identity reader, the media rules, route advice, Setup command lines and
  exit codes, the helper protocol, the fix (no run without a passed scan, restore point first, restart warning off
  while Setup runs and back on if it stops) and the pre-checks.
- The identity, advice and pre-check code was run read-only on the incident PC (2026-11-19) and reported
  "Windows 11 26H2 · Pro · English (United States) [en-US] · x64", matching the manual check.

Still to test on real Windows (VM with snapshots, per docs/PLAN.md §5):

- Mounting an ISO (`Mount-DiskImage`) and reading `install.wim` / `install.esd` through the helper process.
  In particular, confirm that a 26H2 ISO's image reports build **26300** (not the 26100 base); if it reports 26100,
  the "older version" rule needs to know the 24H2/25H2/26H2 family.
- `setup.exe /Compat ScanOnly` exit codes as documented: matching ISO → 0xC1900210; 25H2 or 24H2 ISO on a
  26H2 PC, different language, Enterprise evaluation ISO → 0xC1900204 or similar.
- `setup.exe /Auto Upgrade` from the app: Setup's window shows, the restart warning stands down, Setup restarts
  the PC, and the exit code when it returns instead (cancelled by the user, or a failure).
- BitLocker status from WMI when the app runs elevated; `ms-settings:recovery` opening from the elevated app.
- Windows 10 22H2 (no Settings route; is Microsoft's ISO still offered?).
