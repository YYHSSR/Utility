# Utility

Windows desktop utility for **Lenovo Legion Y7000 IRX9 (`83JJ`)**, built with **C# / WPF / .NET 10** (`net10.0-windows`, `win-x64`).

## Features

- **F1 Performance Profiles**: Preserves the owner's separate **Daily** and **Gaming** baseline profiles (plus pristine comparison references), displays live verified Lenovo thermal mode and Windows power mode separately, supports stock `Quiet` / `Balanced` / `Performance` fallbacks, and gates full-profile writes until custom fan curves are captured.
- **F2 Battery Conservation**: Reads AC/battery power, charge percentage, design and full-charge capacities, computed health percentage, and cycle count via Win32 battery IOCTLs, plus reads/toggles Lenovo Conservation Mode via `\\.\EnergyDrv` (`0x831020F8`) with read-back verification.
- **F3 Hardware Monitoring**: Displays CPU utilization, physical memory, separate disk capacity and disk activity, NVIDIA RTX telemetry via trusted `nvidia-smi.exe` (with battery-mode dGPU pause and manual refresh), and explicit `Unavailable` / `Stale` / `PausedOnBattery` / `0` status differentiation.
- **F4 Graphics Mode**: Queries Lenovo graphics routing mode, previews transitions requiring display interruption or Windows restart, persists pending mode separately, and requires confirmation of an on-machine recovery path before switching.
- **F5 Software & Startup Management**: Enumerates 32-bit/64-bit Uninstall registry views and current-user AppX packages (without `Win32_Product`), startup Run/folder/scheduled-task entries, deep links to Windows Installed Apps and Startup Apps, and an on-demand **read-only** Lenovo leftover scanner.
- **F6 Custom Fan Curves**: Profile fan-curve editor, live geometric preview, validation against firmware bounds, and pending-capture gating until curves are captured from Legion Zone.
- **F7 Official Drivers**: `Win32_PnPSignedDriver` inventory grouped by category, links to Lenovo 83JJ drivers, NVIDIA drivers, Windows Update, and Windows Optional Updates, plus Authenticode + SHA-256 + vendor + architecture + BIOS-exclusion verification for local official `.exe` installers.

## Prerequisites & Build / Publish

- **.NET 10 SDK (`10.0.404`)** to build from source, or **.NET 10 Windows Desktop Runtime (`10.0.15` x64)** to run the framework-dependent `win-x64` package.
- Utility does **not** install any background Windows service or kernel driver.

```powershell
dotnet --list-sdks
dotnet restore
dotnet build -c Release --no-restore
dotnet test -c Release --no-build
dotnet publish .\src\Utility.App\Utility.App.csproj -c Release -r win-x64 --self-contained false -o .\publish\win-x64
```

### Installing / Running & Removing

- **Run**: Launch `Utility.exe` from `publish\win-x64\` as a standard user. Administrator elevation is requested on demand via `Utility.ElevatedWorker.exe` only when invoking privileged Lenovo WMI operations.
- **Diagnostics CLI (read-only)**: `Utility.exe --diagnostics-export <output.json>` exports a standard-user capability and telemetry snapshot and exits cleanly.
- **Removal**: Close `Utility.exe` and delete the application folder plus `%LOCALAPPDATA%\Utility\` (where `settings.json`, `settings.lastgood.json`, `operation-journal.json`, and `diagnostics.log` are stored).

## Icon Asset

- Source image: `Utility-icon-source.png` (kept intact at repository root and copied to `assets/Utility-icon-source.png`).
- Derived multi-resolution `.ico` (`16, 24, 32, 48, 64, 128, 256` px): generated reproducibly via `E:\minconda\envs\py13\python.exe scripts\generate_icon.py` into `assets/Utility.ico`.

## Repository Layout

- `src/Utility.Core/` — Domain models, profile seeds, validation, state persistence, write coordinator, leftover classifier, driver evaluator.
- `src/Utility.Platform/` — Win32 battery/power/trust/metrics interop, `EnergyDrv` IOCTL client, Lenovo WMI provider, elevated named-pipe client, Windows inventory providers.
- `src/Utility.ElevatedWorker/` — Narrowly scoped `requireAdministrator` helper binary accepting only typed `ElevatedOperation` requests over an ACL-restricted named pipe.
- `src/Utility.App/` — WPF desktop shell, `zh-CN` default localization resources (`Strings.zh-CN.resx`), ViewModels, and Views.
- `tests/Utility.Tests/` — Automated xUnit tests using simulated hardware providers (zero real hardware writes).
- `docs/` — Architecture notes and machine capability/mapping report.
