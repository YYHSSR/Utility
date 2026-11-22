# Utility — Target Capability & Field-Mapping Report (`LENOVO 83JJ`)

## 1. Target Baseline Observed

| Item | Observed Value |
| --- | --- |
| Machine | `LENOVO 83JJ` (`Legion Y7000 IRX9`) |
| BIOS | `PTCN18WW` |
| Operating System | Windows 11 Home x64, Build `26300` |
| Development / Runtime | .NET SDK `10.0.404`, Windows Desktop Runtime `10.0.15` |
| Test Session Privilege | Standard user (`IsElevated = false`) |
| Vendor Drivers Present | `Lenovo Fn and function keys` `2.1.2602.8` (`oem85.inf`), `Lenovo ACPI-Compliant Virtual Power Controller` `15.11.30.14` (`oem86.inf`) |

## 2. Standard-User vs. Elevated Capability Matrix

| Feature | Standard-User Read | Standard-User Write | Status Observed | Provider & Live Evidence |
| --- | :---: | :---: | --- | --- |
| **F1** Performance profiles | Partial (Windows overlay only) | No | `InsufficientPrivilege` | `powrprof.dll` read `BestPerformance`; `LENOVO_GAMEZONE_DATA` & `LENOVO_OTHER_METHOD` returned `AccessDenied`. |
| **F2** Battery conservation | Yes | Yes (not mutated in automated run) | `Available` | `IOCTL_BATTERY_QUERY_INFORMATION` (`60000 mWh` design, `59410 mWh` full, `99.02%` health, `85` cycles); `\\.\EnergyDrv` `IOCTL 0x831020F8(0xFF) -> 0x10860A00` (`Normal`). |
| **F3** Hardware monitoring | Yes (CPU/RAM/Disk/GPU) | N/A | `Available` | `GetSystemTimes`, `GlobalMemoryStatusEx`, `DriveInfo` + `PhysicalDisk` counters, `C:\Windows\System32\nvidia-smi.exe` (RTX 4060 Laptop GPU `7%`, `60 C`, `19.3 W`, `2378/8188 MB`, `P0`). CPU package temp & fan RPM marked `Unavailable` in standard-user mode. |
| **F4** Graphics mode | No | No | `InsufficientPrivilege` | `LENOVO_GAMEZONE_DATA` (`IsSupportGSync`, `GetGSyncStatus`, `GetIGPUModeStatus`) returned `AccessDenied` in standard-user mode. |
| **F5** Software & startup | Yes | Links only | `Available` | `221` installed apps (Registry + AppX, no `Win32_Product`), `50` startup entries, `12` read-only Lenovo findings (`SystemIdleMonitor` -> `StaleAction`; `ContextMenuExt` / `A0PcmShellExtension`, `LegionZoneExt`, `LegionLightingController`, `LZService`, `GAService`, `LenovoPcManagerService` -> `ActiveInstalledComponent`; `ImControllerService`, `LenovoFnAndFunctionKeys` -> `SharedOrExcluded`). |
| **F6** Custom fan curves | No | No | `InsufficientPrivilege` + Pending Capture | `LENOVO_FAN_TABLE_DATA` returned `AccessDenied`; Daily and Gaming curves remain `PendingCapture`. |
| **F7** Official drivers | Yes | Verified `.exe` check | `Available` | `233` signed drivers via `Win32_PnPSignedDriver`; `WinVerifyTrust` + SHA-256 + publisher/architecture/OS/BIOS-exclusion evaluator implemented and tested. |

## 3. Section 3 Profile Seed & WMI Mapping Table

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

1. Run an elevated capability probe (`Utility.ElevatedWorker.exe --probe-report <path>` or via the **Capabilities & Diagnostics** tab) to confirm `LENOVO_CAPABILITY_DATA_01` min/max/step values, TCC encoding, and live `GetGSyncStatus`/`GetIGPUModeStatus` on BIOS `PTCN18WW`.
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
