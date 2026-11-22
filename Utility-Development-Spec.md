# Utility — development specification and acceptance criteria

**Audience** — Gemini implements and validates the application. Codex reviews the source, build, and machine-specific evidence before the owner relies on Utility in place of Lenovo PC Manager and Legion Zone.

## 1. Product and scope

Utility is a Windows desktop application for the owner's Lenovo Legion Y7000 IRX9. The first release includes seven functions.

| ID | Feature | Required outcome |
| --- | --- | --- |
| F1 | Performance profiles | Preserve the owner's two distinct **Daily** and **Gaming** profiles. Apply and verify a selected profile on supported hardware. Show the verified live thermal mode separately from the selected profile. |
| F2 | Battery conservation | Show battery and AC status, show battery health when available, and read and toggle the actual Lenovo conservation feature. |
| F3 | Hardware monitoring | Show CPU, GPU, memory, and disk activity. Show temperatures and fan RPM when the respective source is available. |
| F4 | Graphics mode | Show the current Lenovo graphics mode and switch between supported modes, including Hybrid and dGPU-only where verified. |
| F5 | Software and startup management | List installed apps and startup entries, give access to normal Windows management actions, and provide a read-only scan for Lenovo app leftovers. |
| F6 | Custom fan curves | Capture or enter curves for each profile, validate and preview them, apply verified curves, and restore firmware defaults or the previous curve when supported. |
| F7 | Official drivers | Show installed hardware drivers and provide a user-initiated installation path through Windows Update or the official hardware vendor. |

Use a distinct desktop interface with Simplified Chinese as the default user-facing language. Put localizable UI strings in resources. Keep source, technical documentation, file names, and logs in English. Use **Utility** in the application title, executable metadata, Start menu and taskbar entry, and installer display name if an installer is supplied. A single main window with navigation for the seven features is sufficient.

### Icon asset

The owner supplied **[Utility-icon-source.png](./Utility-icon-source.png)**. Keep the supplied PNG intact as the source. Copy it into the repository assets and derive a square, multi-resolution Windows `.ico`, preserving the character, colors, and proportions. Frame the face so it reads at small sizes. Include at least 16, 24, 32, 48, and 256 px frames. Embed the icon in the executable and set it on the main window and shortcuts. Use the same icon in any tray entry provided. Verify the built executable in Explorer, the running taskbar entry, and the Start menu shortcut if present.

## 2. Target environment and evidence

| Item | Observed baseline |
| --- | --- |
| Machine | Lenovo Legion Y7000 IRX9, machine type `83JJ` |
| OS | Windows 11 Home x64, observed build `26300` |
| BIOS | `PTCN18WW` |
| CPU and GPU | Intel Core i7-13650HX and NVIDIA GeForce RTX 4060 Laptop GPU |
| Original software | Lenovo PC Manager `5.1.210.10222` and Legion Zone `2.0.30.11033` |
| Development environment | .NET SDK `10.0.404` and Windows Desktop Runtime `10.0.15`, already installed |
| Intended source directory | `E:\CODE\Utility\`, empty when inspected |

- Windows battery data and NVIDIA GPU temperature and utilization were successfully read on the target. Those observations establish a starting point for monitoring.
- Lenovo WMI classes were discoverable under `root\wmi`. Instance reads of `LENOVO_GAMEZONE_DATA`, `LENOVO_OTHER_METHOD`, `LENOVO_FAN_METHOD`, and `LENOVO_LIGHTING_METHOD` returned access denied in a standard-user session. Elevated access and specific read/write capabilities need separate verification.
- Legion Zone stores a reference to a custom profile in its global game settings. The app's installed UI code indicates that some named custom profile details are fetched through its signed-in online flow. The local fan table cache could not be tied to either user profile. The authoritative values available today are the two screenshots transcribed below.
- Lenovo Fn and function keys `2.1.2602.8` and Lenovo ACPI-Compliant Virtual Power Controller `15.11.30.14` were present. They are examples of device components relevant to independence testing. Record the actual dependencies required by Utility after testing. [Lenovo 83JJ setup guide](https://iknow.lenovo.com.cn/detail/421507?type=0)

## 3. Owner's profile baselines

These are the settings visible in the screenshots. Preserve the two profiles separately and seed them as editable copies, with pristine reference values available for comparison. A profile seed is a proposed configuration. Only a successful hardware read-back establishes the active configuration.

| Screenshot field | Daily | Gaming |
| --- | ---: | ---: |
| CPU Long Term Turbo Power Limit | 55 W | 55 W |
| CPU Long Term Turbo Power Limit (Cross Loading) | 45 W | 55 W |
| CPU Long Term Turbo Power Limit Tau | 56 | 56 |
| CPU Short Term Turbo Power Limit | 100 W | 100 W |
| CPU Thermal Control Circuit | 85 °C | 90 °C |
| GPU Dynamic Boost | 20 W | 0 W |
| GPU To CPU Dynamic Boost | 0 W | 0 W |
| GPU Configurable TGP | 85 W | 115 W |
| GPU Target Temperature Limit | 80 °C | 83 °C |
| Total Processor Power Target In AC | 45 W | 55 W |
| Windows power mode selected in Legion Zone | Best performance | Best performance |
| Fan mode selected | Custom curve | Custom curve |
| Fan curve points for each fan or channel | **Pending capture** | **Pending capture** |

Keep the three different CPU power fields and the AC processor target separate. Keep Configurable TGP and Dynamic Boost separate. Verify Tau units and the encoding of the CPU thermal setting against this firmware. The displayed CPU temperature may need conversion to the backend's representation. Record the conversion, range, step size, prerequisites, and tested read-back for every mapped field. Preserve valid zero values. Prevent applying a draft with missing or unverified hardware fields.

Capture the two fan curves through Legion Zone's visible profile editor or another verified read-only source before uninstalling Legion Zone. Capture all temperature and speed points, units, and fan/channel mapping for each profile. Keep a curve missing until its source and ownership are known. A file-level fan table cache and a reference to a custom profile do not establish the two curves. If the owner cannot provide the curves during development, keep the editor and preview functional, report the pending capture, and leave full-profile application unavailable for any incomplete profile. If a separately labeled action supports applying the verified power fields alone, preview that scope and preserve the existing fan policy.

## 4. Architecture and implementation

Use **C# / WPF / .NET 10**, targeting `net10.0-windows` and publishing for `win-x64`. Run `dotnet` from the existing PATH. Use standard WPF data binding and async view models. Keep the project layout small. Separate presentation, device/state operations, and integration with Windows or Lenovo so tests can substitute a provider without enabling real hardware writes. Use an application manifest with the default desktop UI running as the current user. [Microsoft .NET support](https://learn.microsoft.com/en-us/dotnet/core/releases-and-support) · [WPF documentation](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/)

- **Capability detection.** Build a per-feature result with read availability, write availability, reason, provider, and last checked time. Distinguish unsupported machine, unavailable interface, insufficient privilege, transient failure, and reboot pending. Validate the model and firmware capabilities before writes. On another machine or after firmware changes, require a fresh capability check before treating a prior mapping as valid.
- **Device interface.** Start by verifying Lenovo WMI and existing vendor components. Relevant discovery candidates include `LENOVO_GAMEZONE_DATA` for mode and sensors, `LENOVO_FAN_METHOD` for fan tables, and `LENOVO_OTHER_METHOD` for individual feature values. A discovered method name is not evidence for valid arguments, response codes, or operation on this machine. Document the provider, input/output contract, units, capability checks, and observed behavior before exposing each write. Avoid undocumented direct EC register writes and custom kernel drivers for this release. Lenovo Legion Toolkit is a technical reference. If code is reused, record provenance, pin the revision, and satisfy its GPL-3.0 terms before distributing the resulting build. [Lenovo Legion Toolkit source and license](https://github.com/LenovoLegionToolkit-Team/LenovoLegionToolkit)
- **Elevation.** Test under a normal account first. If a confirmed operation requires elevation, request it on demand for that operation. Keep requests typed and narrowly scoped. Resolve and validate device identity, ranges, and arguments again at the privileged boundary. If using an elevated worker, launch a trusted installed binary, use a restricted channel, and reject unknown operations and arbitrary shell commands, executable paths, registry paths, or WMI arguments. A cancelled UAC prompt leaves the previous state intact.
- **State.** Store settings, profiles, migration metadata, and bounded local diagnostics under `%LOCALAPPDATA%\Utility\`. Use versioned JSON, atomic file replacement, and a recoverable last-good copy for profile edits. Keep the editable profile, last verified live state, and any pending reboot request distinct. Never infer a successful hardware write from a saved settings file. Record operation, outcome, and diagnostic error code without account credentials, machine serial numbers, or unrelated registry values.
- **Lifecycle.** Keep hardware writes bound to explicit user actions. Starting Utility, restoring its window, logging in, or resuming from sleep refreshes state. These events must not reapply an old profile. Pause or reduce polling when the window is closed or hidden. Keep hardware operations off the WPF UI thread and cancel or time out slow queries.
- **Hardware write sequence.** Serialize hardware changes. Read a fresh state snapshot, preflight all fields and recovery coverage, and show the requested changes. Before the first write, persist an in-progress record containing the operation ID, baseline, and requested settings. Apply in the verified device-specific order and read back the result. Treat a verified same-state request as a no-op. If an operation fails midway, stop the remaining writes and restore changed values where verified. Re-read after attempted recovery. Persist the final result and report the exact fields that failed or could not be verified. On interruption or app crash, refresh and identify the unfinished operation at the next launch. Use the operation snapshot for recovery only after the user chooses to proceed.

## 5. Functional requirements

### F1. Performance profiles

- Show Daily and Gaming and the stored values from section 3. Support viewing, editing, and explicitly applying either profile independently.
- Show the Lenovo thermal mode and Windows power mode as distinct values. Where supported, expose stock Quiet, Balanced, and Performance as manual fallback controls. Switching a Windows plan or slider alone does not switch the Lenovo firmware mode.
- Observe external mode changes, including `Fn+Q` where the firmware exposes them, without forcing the previous setting back. If read-back only confirms a generic Custom mode, label it Custom. Use the Daily or Gaming label as active only when enough live settings match that full profile.
- Detect AC state from Windows. Enforce verified adapter and power prerequisites before applying a profile. If Windows power mode cannot be mapped on the active scheme, explain which part of the profile remains unapplied. Confirm any legitimate firmware rounding using documented or measured step sizes.

### F2. Battery conservation

- Show AC or battery power, charge percentage, charging status, and available design and full-charge capacities. Calculate health as full-charge capacity divided by design capacity using consistent units. Show unknown values as unavailable. Display cycle count only when the intended battery source provides a valid value. Use `GetSystemPowerStatus` or the documented Windows battery interfaces for basic readings. [Microsoft battery information](https://learn.microsoft.com/en-us/windows/win32/power/battery-information)
- Show the actual current conservation state as read from Lenovo's supported interface. Toggle it and verify read-back. If the firmware exposes a fixed conservation policy, present it as a switch and show its charging band only when verified. A successful toggle verifies the policy setting. Physical charging behaviour may need separate observation across the firmware's charging thresholds.

### F3. Hardware monitoring

- Show CPU usage, CPU temperature when available, installed and used RAM, disk capacity and activity, RTX utilization and temperature, and valid fan RPM. Disk capacity and disk activity are separate signals. Never label a generic ACPI thermal-zone reading as CPU package temperature without confirming its identity.
- Use Windows performance counters or native APIs for system usage. Use NVIDIA's NVML or driver-supplied `nvidia-smi` for verified NVIDIA readings. If using `nvidia-smi`, use the trusted installed executable with explicit arguments, an execution timeout, and structured parsing of missing values. Keep fast-changing telemetry on a bounded polling interval, initially around two seconds while visible. If dGPU sleep cannot be reliably detected without waking it, stop background or battery-mode polling by default and allow a visible manual refresh. [NVIDIA NVML reference](https://docs.nvidia.com/deploy/nvml-api/latest/nvml-api-reference.html) · [NVIDIA query examples](https://nvidia.custhelp.com/app/answers/detail/a_id/3751/)
- Display units, source errors, and stale states clearly. Distinguish a measured `0 RPM` or `0%` from a failed or unavailable reading. Refresh the providers on resume and tolerate a GPU that disappears during a mode switch.

### F4. Graphics mode

- Query the actual current graphics configuration and enumerate only modes verified on the target. Lenovo Hybrid/dGPU-only mode concerns system graphics routing and differs from Windows per-app graphics preferences. Add any iGPU-only or automatic hybrid option only after verifying support.
- Preview the requested mode and indicate whether the transition may interrupt displays or requires a reboot. Persist the pending selection separately and let the user initiate the restart. Recheck effective mode after restart or resume. Keep the old effective mode visible until the new mode has been verified.
- Confirm that the owner can recover to a working graphics mode via an on-machine option before testing a routing change. Reject unsupported hot switches and report failure or mismatch without retry loops.

### F5. Software and startup management

- Enumerate the 32-bit and 64-bit user and machine uninstall registry views and current-user AppX/MSIX packages. Show name, publisher, version, and installation scope where known. Use stable IDs to deduplicate inventory. Do not enumerate with `Win32_Product`, which can trigger installer consistency checks. [Microsoft Windows Installer guidance](https://learn.microsoft.com/en-us/troubleshoot/windows-server/admin-development/windows-installer-reconfigured-all-applications)
- Open Windows Installed Apps for standard uninstall actions. Use a supported app-specific deep link when available and fall back to the list. Preserve vendor uninstall choices and prompts. Avoid directly executing raw registry `UninstallString` values.
- Inventory user and system startup folders, Run entries, and relevant logon/startup scheduled tasks. Show source and current status when ascertainable. Provide Windows Startup Apps as the normal control for supported entries. If adding inline enable/disable for an entry type, preserve the exact original entry, make the action reversible, and verify the new state. Keep services and kernel drivers out of the general startup toggle list.
- Provide an on-demand **read-only** Lenovo leftover scan. Show location, evidence linking an item to the product, current state, and whether the main app is still installed. Distinguish a stale action, active installed component, and candidate leftover after uninstall. Examples already observed include the stale `SystemIdleMonitor` task pointing to a missing old PC Manager file, the `A0PcmShellExtension` package, the `LegionZoneExt` and `LegionLightingController` packages, and `LZService` and `GAService`. Exclude unrelated or shared Lenovo components from cleanup suggestions when ownership is uncertain. Do not delete Lenovo directories, registry keys, packages, services, or drivers from the scan. [Microsoft Windows Settings launch URIs](https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings)

### F6. Custom fan curves

- Show the active policy, all editable points and their units, fan/channel association, and a preview for each stored curve. Use firmware-provided channel count, point count, order, steps, and limits where verified. If the firmware couples channels or uses multiple temperature sources, represent its actual table structure and validate that whole structure.
- Reject malformed or out-of-range points before writing. Do not guess missing points or extrapolate a curve. Use the firmware-supported table and policy so cooling remains governed by the device when Utility exits. Apply curves only under the verified prerequisites. Verify table read-back and restore the previous table or firmware defaults when those actions have been validated on this machine.
- Start on-machine validation at idle with an existing curve or firmware defaults. Check actual temperature and RPM response with normal controlled load before declaring the write path usable. If temperature or RPM visibility, restoration, or policy verification fails, stop write testing and show read-only or unavailable controls until resolved.

### F7. Official drivers and installation

- Inventory drivers for devices such as graphics, Wi-Fi, audio, chipset, and Lenovo device control. Show device, provider, installed version, driver date, and INF name when available. Use `Win32_PnPSignedDriver` or SetupAPI for inventory. Include distinct links to the Lenovo 83JJ downloads page, NVIDIA's official driver page, Windows Update, and Windows Optional Updates. [Microsoft driver inventory class](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/whqlprov/win32-pnpsigneddriver) · [Microsoft Windows Update driver distribution](https://learn.microsoft.com/en-us/windows-hardware/drivers/dashboard/understanding-windows-update-automatic-and-optional-rules-for-driver-distribution) · [Lenovo 83JJ drivers](https://pcsupport.lenovo.com/us/en/products/laptops-and-netbooks/legion-series/legion-5-15irx9/83jj/downloads/driver-list) · [NVIDIA drivers](https://www.nvidia.com/en-us/drivers/)
- Provide two supported user-driven install routes. For Windows Update use `ms-settings:windowsupdate-optionalupdates` and let Windows handle the chosen update. For Lenovo and NVIDIA packages, link to the official page for the user to obtain the package. Utility may launch a selected official vendor `.exe` when it can verify provenance and applicability and the user confirms the device, version, and publisher. Otherwise direct the user to the vendor's own installation route. Allow only documented vendor arguments and preserve the interactive installer and its restart prompt.
- A valid file signature establishes publisher trust, not compatibility. Before an in-app launch use Windows trust verification and confirm the expected publisher, official package identity, target device/model, architecture, and OS support. Verify the actual bytes to be launched. For INF/catalog packages, defer to the OS/vendor installer in this release. Keep ordinary driver installation distinct from BIOS or firmware flashing, which has no Utility launch action. [Microsoft WinVerifyTrust](https://learn.microsoft.com/en-us/windows/win32/api/wintrust/nf-wintrust-winverifytrust) · [Microsoft driver catalog signing](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/catalog-files)
- Keep discovery separate from installation. Take no action solely because an installed version looks older. Do not silently download, install, downgrade, remove, or force a driver. After an installation action, refresh inventory and indicate any restart requirement reported by the installer or OS. Report observed changes rather than assuming installation success from process exit alone.

## 6. Implementation sequence and handoff

1. **Establish the baseline.** Deliver the WPF shell and icon, read-only Windows inventory, telemetry, and a small on-machine capability report. Record standard-user and elevated results separately wherever their behaviour differs. Identify missing fan data and control mappings.
2. **Implement and validate controls.** Start with whichever battery or Lenovo mode operation can be read, written, and restored reliably. Validate individual profile fields and the fan interface before composing full-profile writes. Add graphics switching only after confirming the recovery path. Keep unsupported and incomplete paths visible with actionable status.
3. **Finish software and driver workflows.** Add Windows management links, scoped inventory, Lenovo leftover classification, and official driver installation routes. Verify that the install flow never mutates anything until a user chooses an action.
4. **Prove replacement readiness.** Test while the original apps are installed, then test independence without relying on their resident services where this can be verified without an uninstall. Perform any disabling, uninstall, or physical-machine restart only with the owner directing that action. After the owner approves and uses normal uninstall procedures, repeat the target-machine acceptance checks. Identify any required Lenovo device driver or Windows component and any remaining dependence on the original applications.

Gemini's handoff should include source, the buildable solution, the packaged `win-x64` app, the icon source and derived asset, and a short build/run guide. Include a field-mapping and capability record with verified operations, conditions, read-back evidence, and unresolved cases. Give Codex the repository or commit and exact build/test commands, plus a clean review report that identifies the target machine, firmware, and steps performed without including personal identifiers. A build can use a framework-dependent publish with the installed .NET 10 desktop runtime or a self-contained publish. Document the choice and installation/removal steps. The package may not install a background service or kernel driver as a prerequisite for launching the desktop UI.

Expected verification commands, run from the directory containing the solution. Adjust the project path to the checked-in layout.

```powershell
dotnet --list-sdks
dotnet restore
dotnet build -c Release --no-restore
dotnet test -c Release --no-build
dotnet publish .\src\Utility.App\Utility.App.csproj -c Release -r win-x64 --self-contained false
```

## 7. Codex acceptance and review

| Area | Evidence required |
| --- | --- |
| Build and branding | A clean Release build, the packaged app starts on Windows 11 as a standard user, and the owner-provided image is recognizable in the actual Windows icon. |
| Profiles | Daily and Gaming are seeded with the exact screenshot values. On the 83JJ, the verified fields apply to the intended profile and read back within verified hardware steps. Saving a draft, external `Fn+Q` changes, and cancelling elevation do not silently change hardware. Full profile parity stays pending until both curves are captured and compared. |
| Battery | An owner-authorized on/off round trip returns the actual Lenovo policy to its original state. Basic battery readings work independently of the conservation control. |
| Telemetry | Cross-check RTX readings with an NVIDIA tool and validate CPU, disk, and memory against their stated Windows sources. Validate fan readings against a trusted device source when available. Test stale samples, unplugging, idle and resume, and an unavailable or sleeping GPU. |
| Graphics | Compare effective mode before change, requested mode, and effective mode after any required restart. Verify that cancel preserves state and that the original mode can be restored. |
| Software and startup | Compare representative inventory items with Windows Settings. Verify that listing and scanning are read-only, and that startup management and uninstall correctly open the Windows UI. Confirm correct handling of the stale task and the separate application packages. |
| Fan curves | Show each fan/channel's verified curve and units, enforce device limits, and demonstrate read-back and restoration on the target. Include the owner-confirmed curve capture or mark it pending. |
| Driver routes | Confirm inventory against Windows Device Manager on sample devices. Test the OEM and Windows Update links. If direct package launch is implemented, test acceptance of a verified official matching package and rejection of mismatched or untrusted input. Only install a driver on the owner's machine when the owner has chosen the specific update. |
| Recovery and independence | Exercise access denied, unsupported hardware, stale reads, mid-operation failure, UAC cancel, and reboot pending. Show whether the normal features still work once the two OEM apps have been removed. If that test has not been authorized or completed, report it as pending. |

Automate verification for profile seeds, validation and unit conversion, missing versus zero values, versioned settings, driver applicability decisions, and provider failure/recovery paths. Use simulated providers in automated tests and report simulation separately from real machine validation. Hardware-writing tests must be opt-in and require the exact target, operator initiation, and a recorded path back to the original state.

The acceptance record must identify the code reviewed, build and test results, functions verified on 83JJ, defects or blockers, and any remaining requirement for Lenovo software. Mark Utility ready to replace the original apps only after the selected functions work independently and the owner's two profiles have been verified to the extent the owner expects.
