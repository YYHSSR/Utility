# Utility Architecture

## Solution Structure

- **Utility.Core** (`net10.0-windows`): Pure domain models, validation logic, atomic JSON state store, hardware write coordinator, read-only leftover classifier, and driver applicability evaluator. Has zero UI or privileged OS dependencies so all core rules are unit-tested with simulated providers.
- **Utility.Platform** (`net10.0-windows`): Windows and Lenovo hardware integration (`Win32BatteryInterop`, `EnergyDrvInterop`, `PowerSchemeInterop`, `SystemMetricsInterop`, `WinTrustInterop`, `LenovoWmiProvider`, `ElevatedWorkerClient`, and Windows inventory providers).
- **Utility.ElevatedWorker** (`WinExe`, `requireAdministrator`): Minimal privileged helper bundled alongside `Utility.exe`. Accepts only `--pipe <name> --token <hex>` (or `--probe-report <path>`), validates `LENOVO 83JJ` identity and live `LENOVO_CAPABILITY_DATA_01` bounds at the privileged boundary, and executes only the closed `ElevatedOperation` enum.
- **Utility.App** (`WinExe`, WPF): Standard-user (`asInvoker`) desktop UI defaulting to Simplified Chinese (`zh-CN` via `Strings.zh-CN.resx`). Pauses fast telemetry polling when minimized/hidden and refreshes read-only state on power resume without reapplying profiles.

## Privilege Boundary & Write Safety

1. Standard-user operations run directly in `Utility.exe`:
   - Win32 battery telemetry (`IOCTL_BATTERY_QUERY_INFORMATION`) and `\\.\EnergyDrv` (`0x831020F8`) conservation read/toggle.
   - CPU/RAM/Disk metrics and `C:\Windows\System32\nvidia-smi.exe` GPU telemetry.
   - Registry/AppX/TaskScheduler/Service software & leftover inventory and `Win32_PnPSignedDriver` driver inventory.
   - `WinVerifyTrust` + SHA-256 verification of local official `.exe` driver packages.
2. Privileged Lenovo WMI operations (`root\wmi` classes `LENOVO_GAMEZONE_DATA`, `LENOVO_OTHER_METHOD`, `LENOVO_CAPABILITY_DATA_01`, `LENOVO_FAN_TABLE_DATA`) trigger on-demand UAC via `ElevatedWorkerClient` over a `NamedPipeServerStream` secured with a `PipeSecurity` ACL restricted to the current user SID and `BuiltinAdministratorsSid` plus a per-session 256-bit random auth token.
3. All profile writes serialize through `HardwareWriteCoordinator`, write an atomic `InProgress` journal record under `%LOCALAPPDATA%\Utility\operation-journal.json` before mutating hardware, perform post-write read-back verification, and roll back changed fields if a step fails midway.
