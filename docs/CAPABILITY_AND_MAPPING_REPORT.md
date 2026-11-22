# Utility — Target Capability & Field-Mapping Report (`LENOVO 83JJ`)

## Verified in Utility 0.4.2

- The dashboard's read-only elevated session returned CPU package temperature, both fan RPM readings, and all ten Daily power limits. The desktop app continued running as a standard user.
- EnergyDrv reported normal charging at `0x10860A00`, conservation at `0x10860820`, and normal charging again after restoration.
- Changing the final fan stage from level 9 to 10 was verified through the Lenovo fan capture. Restoring the original ten levels was verified through the same read.
- While booted in discrete mode, Utility submitted Hybrid and Hybrid iGPU-only requests, read back the integrated GPU preference, then submitted the original discrete setting and restored the preference. The BIOS MUX getter reports the current boot's routing. Post-restart routing was outside this validation run.

## 0. Verified on the 0.2.1 target

- The active 83JJ / PTCN18WW profile matched all ten original Daily power values. Live Lenovo power methods use the capability ID with the mode byte cleared. The mode-indexed IDs below describe firmware ranges.
- Direct firmware testing verified Daily → Gaming → Daily with all ten power values read back. The captured fan table was unchanged. Firmware writes verify the resulting values because the setter has no reliable success field.
- Utility can switch only the two original power presets, while plugged in and starting from a recognized custom-mode baseline. The Gaming preset's own fan mapping, fan writes, and independence from the running Legion Zone services are still pending.
- The owner's screenshot stages match the active Daily fan table. Select the original Gaming profile in Legion Zone, then use the Utility fan page to read and save its live table separately.


## 1. Target Baseline Observed

| Item | Observed Value |
| --- | --- |
| Machine | `LENOVO 83JJ` (`Legion Y7000 IRX9`) |
| BIOS | `PTCN18WW` |
| Operating System | Windows 11 Home x64, Build `26300` |
| Development / Runtime | .NET SDK `10.0.404`, Windows Desktop Runtime `10.0.15` |
| Test Session Privilege | Standard user (`IsElevated = false`) |
| Vendor Drivers Present | `Lenovo Fn and function keys` `2.1.2602.8` (`oem85.inf`), `Lenovo ACPI-Compliant Virtual Power Controller` `15.11.30.14` (`oem86.inf`) |

## 2. Reviewed capability matrix

Version `0.1.1` was tested on a standard-user session. A readable interface does not establish a working or reversible write.

| Feature | Evidence | Release state |
| --- | --- | --- |
| F1 Performance | Two screenshot baselines preserved. Standard-user Lenovo firmware access denied. Windows power mode readable. | Editable drafts; hardware writes pending. |
| F2 Battery | 60,000 mWh design and 59,410 mWh full-charge capacity, 99.02% health. Raw Lenovo charging state `0x10860A00`, interpreted as Normal. | Read only; controlled conservation write and recovery pending. |
| F3 Monitoring | Valid CPU, memory, disk and RTX readings obtained. | CPU package temperature and fan RPM unavailable. |
| F4 Graphics | Standard-user Lenovo firmware access denied. Routing values have not been validated on this firmware. | Read-only firmware probe; switching pending. |
| F5 Software and startup | 221 app records, 50 startup entries and 12 Lenovo findings at review time. `SystemIdleMonitor` classified as a stale action. | Inventory, Windows links and read-only scan. |
| F6 Fan curves | Simulated persistence preserves separate multi-channel drafts. Standard-user firmware table access denied. The actual curves for both profiles remain uncaptured. | Editable drafts and raw diagnostic; apply and restore pending. |
| F7 Drivers | 233 PnP driver records at review time. | Inventory, signature inspection and official install routes; no direct package launch. |

The Release build, automated tests, published package, main window startup, and standard-user diagnostic export were verified. No firmware, driver, or OEM application settings were changed. The elevated probe still needs end-to-end verification on this machine.

## 3. Profile baselines and provisional field mapping

The candidate IDs below are from the original revision. Their meanings, encodings and units remain unverified on BIOS `PTCN18WW`. The current preview does not use them to write hardware.

| Profile Field | Daily Seed | Gaming Seed | WMI Capability ID (`LENOVO_OTHER_METHOD`) | Unit | Status on `83JJ` |
| --- | ---: | ---: | --- | --- | --- |
| `CpuLongTermPowerLimitW` | 55 | 55 | `0x0102FF00` | W | Pending elevated WMI bounds & read-back verification |
| `CpuCrossLoadingPowerLimitW` | 45 | 55 | `0x0106FF00` | W | Pending elevated WMI bounds & read-back verification |
| `CpuLongTermPowerLimitTau` | 56 | 56 | `0x0107FF00` | s | Pending elevated WMI bounds & tau unit verification |
| `CpuShortTermPowerLimitW` | 100 | 100 | `0x0101FF00` | W | Pending elevated WMI bounds & read-back verification |
| `CpuThermalControlCircuitC` | 85 | 90 | `0x0104FF00` | °C | Pending verification of direct °C vs. TjMax offset encoding |
| `GpuDynamicBoostW` | 20 | 0 | `0x0201FF00` | W | Pending elevated WMI bounds & zero read-back verification |
| `GpuToCpuDynamicBoostW` | 0 | 0 | `0x020BFF00` | W | Pending elevated WMI bounds & zero read-back verification |
| `GpuConfigurableTgpW` | 85 | 115 | `0x0202FF00` | W | Pending elevated WMI bounds & step-size verification |
| `GpuTargetTemperatureLimitC` | 80 | 83 | `0x0203FF00` | °C | Pending elevated WMI bounds & read-back verification |
| `TotalProcessorPowerTargetAcW` | 45 | 55 | `0x0204FF00` | W | Pending verification of direct W vs. baseline offset encoding |

## 4. Pending Owner-Authorized Validation Items

1. Use Capabilities & Diagnostics to run a read-only elevated probe. Correlate raw firmware responses with the Legion Zone UI before adding any write path.
2. Capture both **Daily** and **Gaming** custom fan curves from Legion Zone's visible editor (temperature/speed points, units, and fan/channel mapping) before uninstalling Legion Zone.
3. Perform an owner-authorized conservation mode on/off round trip (`F2`) and verify physical return to `Normal` (`0x10860A00` or current charge state).
4. Perform owner-authorized profile power writes (`F1`), fan table read/restore (`F6`), and graphics mode switching (`F4`) after confirming an on-machine recovery path.
5. After owner-approved uninstallation of Lenovo PC Manager and Legion Zone, re-run acceptance checks to confirm independence and verify required shared drivers (`LenovoFnAndFunctionKeys`, `Lenovo ACPI-Compliant Virtual Power Controller`).

## 5. Codex Review & Verification Commands

Run from `E:\CODE\Utility`:

```powershell
dotnet --list-sdks
dotnet restore
dotnet build -c Release --no-restore
dotnet test -c Release --no-build --verbosity normal
dotnet publish .\src\Utility.App\Utility.App.csproj -c Release -r win-x64 --self-contained false -o .\publish\win-x64
```

Read-only standard-user diagnostics verification:

```powershell
Start-Process -FilePath "E:\CODE\Utility\publish\win-x64\Utility.exe" -ArgumentList "--diagnostics-export", "`"$env:TEMP\utility-diag.json`"" -Wait -PassThru
```
