# WU Health Guard

A Windows tray app that checks Windows Update health **before you restart**.

It flags stuck CBS *Install Pending* / *Uninstall Pending* packages, stale `pending.xml` and reboot
flags, DISM component-store damage and failed updates. It warns you or auto-pauses updates, and offers
safe one-click fixes. Risky fixes need confirmation and take a backup first. Every action is logged.

> **Status: early development.** The checks and fix logic are unit-tested. The Windows-facing parts
> build but haven't been validated on real machines yet. See [docs/PLAN.md](docs/PLAN.md).

## The tray icon

| Icon | Meaning |
|------|---------|
| 🟢 Green | Safe to restart |
| 🔵 Blue | Restart recommended: updates are staged and will finish at the next restart |
| 🟡 Yellow | Caution: fix the update problems first |
| 🔴 Red | Don't restart yet: servicing is running, or the state looks stuck or damaged |

## Build

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

## Run

The app needs administrator rights (DISM API, servicing registry keys, fixes). To start it at logon
without a UAC prompt each time, register the scheduled task once from an elevated prompt:

```bash
WuHealthGuard.exe --install
```

`--uninstall` removes it. Logs and backups go to `%ProgramData%\WuHealthGuard`.

## Layout

| Project | What it holds |
|---------|---------------|
| `src/WuHealthGuard.Core` | Checks, verdict, auto-pause policy, fix runner and fixes. No Windows APIs. |
| `src/WuHealthGuard.Windows` | Registry, DISM API, Windows Update Agent, services, backups, update pause. |
| `src/WuHealthGuard.Tray` | The WinForms tray app. |
| `tests/WuHealthGuard.Core.Tests` | Unit tests for the core, with in-memory fakes. |
