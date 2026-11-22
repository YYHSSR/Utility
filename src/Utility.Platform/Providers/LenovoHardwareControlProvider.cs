using Utility.Core.Models;
using Utility.Core.Services;
using Utility.Platform.Lenovo;
using Utility.Platform.Native;

namespace Utility.Platform.Providers;

/// <summary>
/// Coordinates standard-user WMI probes and on-demand elevated worker IPC calls for Lenovo controls.
/// </summary>
public sealed class LenovoHardwareControlProvider : ILenovoHardwareControlProvider
{
    private readonly LenovoWmiProvider _wmi = new();
    private readonly ElevatedWorkerClient _elevated = new();
    private readonly IBatteryProvider _battery;
    private readonly ITelemetryProvider _telemetry;
    private readonly JsonStateStore _stateStore;

    public LenovoHardwareControlProvider(
        IBatteryProvider battery,
        ITelemetryProvider telemetry,
        JsonStateStore stateStore)
    {
        _battery = battery;
        _telemetry = telemetry;
        _stateStore = stateStore;
    }

    public Task<MachineIdentity> GetMachineIdentityAsync(CancellationToken ct = default) =>
        Task.Run(_wmi.ReadMachineIdentity, ct);

    public async Task<CapabilityReport> ProbeCapabilitiesAsync(bool allowElevation, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var machine = await GetMachineIdentityAsync(ct).ConfigureAwait(false);
        var isElevated = LenovoWmiProvider.IsProcessElevated();

        if (!isElevated && allowElevation)
        {
            var elevatedResp = await _elevated.ExecuteRequestAsync(new ElevatedRequest
            {
                AuthToken = string.Empty,
                Operation = ElevatedOperation.ProbeCapabilities
            }, ct).ConfigureAwait(false);

            if (elevatedResp.Success && elevatedResp.CapabilityReport is not null)
            {
                return elevatedResp.CapabilityReport;
            }
        }

        var batteryCap = await _battery.ProbeCapabilityAsync(ct).ConfigureAwait(false);
        var telemetryCap = await _telemetry.ProbeCapabilityAsync(ct).ConfigureAwait(false);
        var gzCap = _wmi.ProbeWmiClassAccess(FeatureId.LenovoWmiGamezone, LenovoWmiConstants.GameZoneDataClass, "root\\wmi");
        var otherCap = _wmi.ProbeWmiClassAccess(FeatureId.LenovoWmiOther, LenovoWmiConstants.OtherMethodClass, "root\\wmi");
        var fanCap = _wmi.ProbeWmiClassAccess(FeatureId.LenovoWmiFan, LenovoWmiConstants.FanTableDataClass, "root\\wmi");

        var perfStatus = !machine.IsTargetLegion83JJ
            ? CapabilityAvailability.UnsupportedMachine
            : (gzCap.ReadAvailable && otherCap.ReadAvailable)
                ? CapabilityAvailability.Available
                : gzCap.Status;

        var perfCap = new FeatureCapability(
            FeatureId.PerformanceProfiles,
            ReadAvailable: gzCap.ReadAvailable && otherCap.ReadAvailable,
            WriteAvailable: isElevated && gzCap.ReadAvailable && otherCap.ReadAvailable,
            Status: perfStatus,
            Reason: perfStatus == CapabilityAvailability.InsufficientPrivilege
                ? "Lenovo WMI profile control requires on-demand administrator elevation."
                : gzCap.Reason,
            Provider: "LENOVO_GAMEZONE_DATA + LENOVO_OTHER_METHOD + powrprof",
            LastCheckedUtc: now);

        var gfxCap = new FeatureCapability(
            FeatureId.GraphicsMode,
            ReadAvailable: gzCap.ReadAvailable,
            WriteAvailable: isElevated && gzCap.ReadAvailable,
            Status: gzCap.Status,
            Reason: gzCap.Status == CapabilityAvailability.InsufficientPrivilege
                ? "Graphics MUX mode WMI requires on-demand elevation."
                : gzCap.Reason,
            Provider: "LENOVO_GAMEZONE_DATA",
            LastCheckedUtc: now);

        var fanFeatureCap = new FeatureCapability(
            FeatureId.CustomFanCurves,
            ReadAvailable: fanCap.ReadAvailable,
            WriteAvailable: isElevated && fanCap.ReadAvailable,
            Status: fanCap.Status,
            Reason: fanCap.Status == CapabilityAvailability.InsufficientPrivilege
                ? "Fan table read/write requires on-demand elevation; owner Daily/Gaming curves also pending capture."
                : fanCap.Reason,
            Provider: "LENOVO_FAN_TABLE_DATA + LENOVO_FAN_METHOD",
            LastCheckedUtc: now);

        var swCap = new FeatureCapability(
            FeatureId.SoftwareAndStartup,
            ReadAvailable: true,
            WriteAvailable: false,
            Status: CapabilityAvailability.Available,
            Reason: "Registry/AppX/TaskScheduler read-only inventory and Windows Settings links ready.",
            Provider: "Windows Registry + AppModel + Schedule.Service",
            LastCheckedUtc: now);

        var drvCap = new FeatureCapability(
            FeatureId.OfficialDrivers,
            ReadAvailable: true,
            WriteAvailable: true,
            Status: CapabilityAvailability.Available,
            Reason: "Win32_PnPSignedDriver inventory, WinVerifyTrust package check, and Windows Update links ready.",
            Provider: "Win32_PnPSignedDriver + WinVerifyTrust",
            LastCheckedUtc: now);

        var unresolved = new List<string>
        {
            "Daily and Gaming custom fan curves are pending capture from Legion Zone before uninstall.",
            "CPU package temperature and fan RPM sensors are unavailable in standard-user mode."
        };
        if (!isElevated)
        {
            unresolved.Add("Lenovo WMI write capabilities and capability bounds require elevated probe verification on target 83JJ.");
        }

        return new CapabilityReport(
            TimestampUtc: now,
            IsElevated: isElevated,
            Machine: machine,
            Capabilities: [perfCap, batteryCap, telemetryCap, gfxCap, swCap, fanFeatureCap, drvCap, gzCap, otherCap, fanCap],
            UnresolvedItems: unresolved);
    }

    public async Task<LiveThermalSnapshot> ReadLiveThermalSnapshotAsync(bool allowElevation, CancellationToken ct = default)
    {
        if (LenovoWmiProvider.IsProcessElevated() || !allowElevation)
        {
            return await Task.Run(_wmi.ReadLiveThermalSnapshotDirect, ct).ConfigureAwait(false);
        }

        var resp = await _elevated.ExecuteRequestAsync(new ElevatedRequest
        {
            AuthToken = string.Empty,
            Operation = ElevatedOperation.ReadThermalSnapshot
        }, ct).ConfigureAwait(false);

        if (resp.Success && resp.ThermalSnapshot is not null)
        {
            return resp.ThermalSnapshot;
        }

        var fallback = await Task.Run(_wmi.ReadLiveThermalSnapshotDirect, ct).ConfigureAwait(false);
        return fallback with { StatusNote = resp.Message ?? fallback.StatusNote };
    }

    public async Task<OperationResult<ThermalMode>> SetThermalModeAsync(ThermalMode mode, CancellationToken ct = default)
    {
        if (LenovoWmiProvider.IsProcessElevated())
        {
            var ok = _wmi.SetThermalModeDirect(mode, out var msg);
            return ok ? OperationResult<ThermalMode>.Ok(mode, msg) : OperationResult<ThermalMode>.Fail("WMI_SET_FAILED", msg);
        }

        var resp = await _elevated.ExecuteRequestAsync(new ElevatedRequest
        {
            AuthToken = string.Empty,
            Operation = ElevatedOperation.SetThermalMode,
            ThermalMode = mode
        }, ct).ConfigureAwait(false);

        if (resp.WasCancelled) return OperationResult<ThermalMode>.Cancelled(resp.Message ?? "Cancelled.");
        return resp.Success
            ? OperationResult<ThermalMode>.Ok(mode, resp.Message ?? "OK")
            : OperationResult<ThermalMode>.Fail(resp.ErrorCode ?? "ELEVATED_ERROR", resp.Message ?? "Failed to set thermal mode.");
    }

    public Task<OperationResult<WindowsPowerMode>> SetWindowsPowerModeAsync(WindowsPowerMode mode, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var ok = PowerSchemeInterop.TrySetWindowsPowerMode(mode, out var detail);
            return ok
                ? OperationResult<WindowsPowerMode>.Ok(mode, detail)
                : OperationResult<WindowsPowerMode>.Fail("WINDOWS_POWER_MODE_UNMAPPED", detail);
        }, ct);
    }

    public async Task<OperationResult<IReadOnlyDictionary<string, int?>>> ApplyPowerFieldsAsync(
        IReadOnlyDictionary<string, int> backendValuesByField,
        IReadOnlyList<FieldHardwareContract> contracts,
        CancellationToken ct = default)
    {
        if (LenovoWmiProvider.IsProcessElevated())
        {
            var (applied, failed, msg) = _wmi.ApplyPowerFieldsDirect(backendValuesByField);
            if (failed.Count > 0)
            {
                return OperationResult<IReadOnlyDictionary<string, int?>>.Fail("WMI_FIELD_WRITE_FAILED", msg, failed);
            }

            var readBack = _wmi.ReadLivePowerValuesDirect();
            return OperationResult<IReadOnlyDictionary<string, int?>>.Ok(readBack.AsFieldMap(), msg);
        }

        var settings = BuildSettingsFromBackendValues(backendValuesByField, contracts);
        var resp = await _elevated.ExecuteRequestAsync(new ElevatedRequest
        {
            AuthToken = string.Empty,
            Operation = ElevatedOperation.ApplyPowerFields,
            PowerSettings = settings
        }, ct).ConfigureAwait(false);

        if (resp.WasCancelled)
        {
            return OperationResult<IReadOnlyDictionary<string, int?>>.Cancelled(resp.Message ?? "Cancelled.");
        }

        if (!resp.Success)
        {
            return OperationResult<IReadOnlyDictionary<string, int?>>.Fail(
                resp.ErrorCode ?? "POWER_WRITE_FAILED",
                resp.Message ?? "Failed to apply power fields.",
                resp.FailedFields);
        }

        return OperationResult<IReadOnlyDictionary<string, int?>>.Ok(
            resp.ReadBackPowerValues ?? new Dictionary<string, int?>(),
            resp.Message ?? "Applied.");
    }

    public async Task<OperationResult<FanCurveProfile>> ReadFanCurveAsync(bool allowElevation, CancellationToken ct = default)
    {
        if (LenovoWmiProvider.IsProcessElevated())
        {
            try
            {
                return OperationResult<FanCurveProfile>.Ok(_wmi.ReadFanCurveDirect());
            }
            catch (Exception ex)
            {
                return OperationResult<FanCurveProfile>.Fail("FAN_READ_FAILED", ex.Message);
            }
        }

        if (!allowElevation)
        {
            return OperationResult<FanCurveProfile>.Fail("INSUFFICIENT_PRIVILEGE", "Reading firmware fan tables requires elevation.");
        }

        var resp = await _elevated.ExecuteRequestAsync(new ElevatedRequest
        {
            AuthToken = string.Empty,
            Operation = ElevatedOperation.ReadFanTables
        }, ct).ConfigureAwait(false);

        if (resp.WasCancelled) return OperationResult<FanCurveProfile>.Cancelled(resp.Message ?? "Cancelled.");
        return resp.Success && resp.FanCurve is not null
            ? OperationResult<FanCurveProfile>.Ok(resp.FanCurve, resp.Message ?? "OK")
            : OperationResult<FanCurveProfile>.Fail(resp.ErrorCode ?? "FAN_READ_FAILED", resp.Message ?? "Failed to read fan tables.");
    }

    public Task<OperationResult<FanCurveProfile>> ApplyFanCurveAsync(FanCurveProfile curve, CancellationToken ct = default) =>
        Task.FromResult(OperationResult<FanCurveProfile>.Fail(
            "FAN_WRITE_NOT_VALIDATED_ON_TARGET",
            "Firmware fan-table write payload requires target 83JJ capture & controlled verification before enabling."));

    public async Task<OperationResult<FanCurveProfile>> RestoreDefaultFanCurveAsync(CancellationToken ct = default)
    {
        var resp = await _elevated.ExecuteRequestAsync(new ElevatedRequest
        {
            AuthToken = string.Empty,
            Operation = ElevatedOperation.RestoreDefaultFanTables
        }, ct).ConfigureAwait(false);

        if (resp.WasCancelled) return OperationResult<FanCurveProfile>.Cancelled(resp.Message ?? "Cancelled.");
        return resp.Success && resp.FanCurve is not null
            ? OperationResult<FanCurveProfile>.Ok(resp.FanCurve, resp.Message ?? "Restored firmware default fan policy.")
            : OperationResult<FanCurveProfile>.Fail(resp.ErrorCode ?? "FAN_RESTORE_FAILED", resp.Message ?? "Restore failed.");
    }

    public async Task<GraphicsStatusSnapshot> ReadGraphicsStatusAsync(bool allowElevation, CancellationToken ct = default)
    {
        var settings = await _stateStore.LoadSettingsAsync(ct).ConfigureAwait(false);
        var pending = settings.Settings.PendingGraphicsMode;

        if (LenovoWmiProvider.IsProcessElevated() || !allowElevation)
        {
            return await Task.Run(() => _wmi.ReadGraphicsStatusDirect(pending), ct).ConfigureAwait(false);
        }

        var resp = await _elevated.ExecuteRequestAsync(new ElevatedRequest
        {
            AuthToken = string.Empty,
            Operation = ElevatedOperation.ReadGraphicsMode
        }, ct).ConfigureAwait(false);

        if (resp.Success && resp.GraphicsStatus is not null)
        {
            return resp.GraphicsStatus with { PendingMode = pending, RebootRequired = pending is not null && pending != resp.GraphicsStatus.EffectiveMode };
        }

        return await Task.Run(() => _wmi.ReadGraphicsStatusDirect(pending), ct).ConfigureAwait(false);
    }

    public async Task<OperationResult<GraphicsStatusSnapshot>> SetGraphicsModeAsync(GraphicsRoutingMode mode, CancellationToken ct = default)
    {
        var resp = await _elevated.ExecuteRequestAsync(new ElevatedRequest
        {
            AuthToken = string.Empty,
            Operation = ElevatedOperation.SetGraphicsMode,
            GraphicsMode = mode
        }, ct).ConfigureAwait(false);

        if (resp.WasCancelled)
        {
            return OperationResult<GraphicsStatusSnapshot>.Cancelled(resp.Message ?? "Cancelled.");
        }

        if (!resp.Success || resp.GraphicsStatus is null)
        {
            return OperationResult<GraphicsStatusSnapshot>.Fail(resp.ErrorCode ?? "GRAPHICS_SWITCH_FAILED", resp.Message ?? "Graphics mode switch failed.");
        }

        var loaded = await _stateStore.LoadSettingsAsync(ct).ConfigureAwait(false);
        await _stateStore.SaveSettingsAsync(loaded.Settings with
        {
            PendingGraphicsMode = resp.GraphicsStatus.RebootRequired ? mode : null,
            LastVerifiedGraphicsMode = resp.GraphicsStatus.EffectiveMode
        }, ct).ConfigureAwait(false);

        return OperationResult<GraphicsStatusSnapshot>.Ok(resp.GraphicsStatus, resp.Message ?? "Graphics mode updated.");
    }

    private static ProfilePowerSettings BuildSettingsFromBackendValues(
        IReadOnlyDictionary<string, int> backendValues,
        IReadOnlyList<FieldHardwareContract> contracts)
    {
        int? Decode(string key)
        {
            if (!backendValues.TryGetValue(key, out var raw)) return null;
            var c = contracts.FirstOrDefault(x => x.FieldKey == key);
            return c is null ? raw : ProfileValidationService.FromBackendValue(raw, c);
        }

        return new ProfilePowerSettings
        {
            CpuLongTermPowerLimitW = Decode(nameof(ProfilePowerSettings.CpuLongTermPowerLimitW)),
            CpuCrossLoadingPowerLimitW = Decode(nameof(ProfilePowerSettings.CpuCrossLoadingPowerLimitW)),
            CpuLongTermPowerLimitTau = Decode(nameof(ProfilePowerSettings.CpuLongTermPowerLimitTau)),
            CpuShortTermPowerLimitW = Decode(nameof(ProfilePowerSettings.CpuShortTermPowerLimitW)),
            CpuThermalControlCircuitC = Decode(nameof(ProfilePowerSettings.CpuThermalControlCircuitC)),
            GpuDynamicBoostW = Decode(nameof(ProfilePowerSettings.GpuDynamicBoostW)),
            GpuToCpuDynamicBoostW = Decode(nameof(ProfilePowerSettings.GpuToCpuDynamicBoostW)),
            GpuConfigurableTgpW = Decode(nameof(ProfilePowerSettings.GpuConfigurableTgpW)),
            GpuTargetTemperatureLimitC = Decode(nameof(ProfilePowerSettings.GpuTargetTemperatureLimitC)),
            TotalProcessorPowerTargetAcW = Decode(nameof(ProfilePowerSettings.TotalProcessorPowerTargetAcW))
        };
    }
}
