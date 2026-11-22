using Utility.Core.Models;
using Utility.Core.Services;
using Utility.Platform.Lenovo;

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

    public LenovoHardwareControlProvider(
        IBatteryProvider battery,
        ITelemetryProvider telemetry)
    {
        _battery = battery;
        _telemetry = telemetry;
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

        var perfCap = ReadOnlyCapability(FeatureId.PerformanceProfiles, gzCap, machine,
            "LENOVO_GAMEZONE_DATA + powrprof",
            "Original preset power limits can be switched after elevation on 83JJ PTCN18WW. Full profiles remain pending.");
        if (isElevated && gzCap.ReadAvailable && otherCap.ReadAvailable &&
            machine.IsTargetLegion83JJ && machine.BiosVersion == "PTCN18WW")
        {
            perfCap = perfCap with { WriteAvailable = true, Status = CapabilityAvailability.Available };
        }
        var gfx = await ReadGraphicsStatusAsync(allowElevation: false, ct).ConfigureAwait(false);
        var gfxCap = new FeatureCapability(FeatureId.GraphicsMode, gfx.IsVerifiedLiveRead, gfx.IsVerifiedLiveRead,
            gfx.IsVerifiedLiveRead ? CapabilityAvailability.Available : gzCap.Status,
            gfx.StatusNote, "LENOVO_GAMEZONE_DATA + Win32_VideoController", now);
        var fanFeatureCap = ReadOnlyCapability(FeatureId.CustomFanCurves, fanCap, machine,
            "LENOVO_FAN_TABLE_DATA + LENOVO_FAN_METHOD",
            "On 83JJ / PTCN18WW, ten fan speed levels can be changed in custom mode on AC.");
        if (fanCap.ReadAvailable && machine.IsTargetLegion83JJ && machine.BiosVersion == "PTCN18WW")
            fanFeatureCap = fanFeatureCap with { Status = CapabilityAvailability.Available, WriteAvailable = true };

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
            WriteAvailable: false,
            Status: CapabilityAvailability.Available,
            Reason: "Driver inventory, signature inspection, and official installation links are available.",
            Provider: "SetupAPI / Win32_PnPSignedDriver + WinVerifyTrust",
            LastCheckedUtc: now);

        var unresolved = new List<string>
        {
            "Power switching preserves the current custom fan curve. The gaming fan curve has no verified capture.",
            "Fan draft geometry is stored separately from the active Lenovo fan table."
        };
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

    public Task<OperationResult<ThermalMode>> SetThermalModeAsync(ThermalMode mode, CancellationToken ct = default) =>
        WritePendingAsync<ThermalMode>("Lenovo thermal mode changes");

    public async Task<OperationResult<LiveThermalSnapshot>> ApplyOwnerPowerPresetAsync(string profileId, CancellationToken ct = default)
    {
        if (LenovoWmiProvider.IsProcessElevated())
            return await Task.Run(() => _wmi.ApplyOwnerPowerPresetDirect(profileId), ct).ConfigureAwait(false);

        var response = await _elevated.ExecuteRequestAsync(new ElevatedRequest
        {
            AuthToken = string.Empty,
            Operation = ElevatedOperation.ApplyOwnerPowerPreset,
            OwnerProfileId = profileId
        }, ct).ConfigureAwait(false);

        if (response.WasCancelled)
            return OperationResult<LiveThermalSnapshot>.Cancelled(response.Message ?? "Cancelled.");
        return response.Success && response.ThermalSnapshot is not null
            ? OperationResult<LiveThermalSnapshot>.Ok(response.ThermalSnapshot, response.Message ?? "Verified.")
            : OperationResult<LiveThermalSnapshot>.Fail(response.ErrorCode ?? "WORKER_UNVERIFIED", response.Message ?? "Switch unverified.");
    }

    public Task<OperationResult<WindowsPowerMode>> SetWindowsPowerModeAsync(WindowsPowerMode mode, CancellationToken ct = default) =>
        WritePendingAsync<WindowsPowerMode>("Windows power mode changes as part of a Lenovo profile");

    public async Task<OperationResult<CoolingCycleResult>> RunCoolingCycleAsync(CancellationToken stop)
    {
        if (LenovoWmiProvider.IsProcessElevated())
            return await Task.Run(() => _wmi.RunCoolingCycleDirect(stop)).ConfigureAwait(false);

        var response = await _elevated.ExecuteRequestAsync(new ElevatedRequest
        {
            AuthToken = string.Empty,
            Operation = ElevatedOperation.RunCoolingCycle
        }, stop).ConfigureAwait(false);

        if (response.WasCancelled)
            return OperationResult<CoolingCycleResult>.Cancelled(response.Message ?? "Cancelled.");
        return response.Success && response.CoolingCycle is not null
            ? OperationResult<CoolingCycleResult>.Ok(response.CoolingCycle, response.Message ?? "Verified.")
            : OperationResult<CoolingCycleResult>.Fail(response.ErrorCode ?? "WORKER_UNVERIFIED", response.Message ?? "Cooling cycle unverified.");
    }

    public Task<OperationResult<IReadOnlyDictionary<string, int?>>> ApplyPowerFieldsAsync(
        IReadOnlyDictionary<string, int> backendValuesByField,
        IReadOnlyList<FieldHardwareContract> contracts,
        CancellationToken ct = default) =>
        WritePendingAsync<IReadOnlyDictionary<string, int?>>("Lenovo power and temperature limit changes");

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
        WritePendingAsync<FanCurveProfile>("Fan table changes");

    public async Task<OperationResult<FanCurveProfile>> SetFanSpeedLevelsAsync(int[] levels, CancellationToken ct = default)
    {
        if (LenovoWmiProvider.IsProcessElevated())
            return await Task.Run(() => _wmi.SetFanSpeedLevelsDirect(levels), ct).ConfigureAwait(false);

        var response = await _elevated.ExecuteRequestAsync(new ElevatedRequest
        {
            AuthToken = string.Empty,
            Operation = ElevatedOperation.SetFanSpeedLevels,
            FanLevels = levels
        }, ct).ConfigureAwait(false);

        if (response.WasCancelled)
            return OperationResult<FanCurveProfile>.Cancelled(response.Message ?? "Cancelled.");
        return response.Success && response.FanCurve is { } curve
            ? OperationResult<FanCurveProfile>.Ok(curve)
            : OperationResult<FanCurveProfile>.Fail(response.ErrorCode ?? "WORKER_UNVERIFIED", response.Message ?? "Fan level change unverified.");
    }

    public Task<OperationResult<FanCurveProfile>> RestoreDefaultFanCurveAsync(CancellationToken ct = default) =>
        WritePendingAsync<FanCurveProfile>("Fan table restoration");

    public async Task<GraphicsStatusSnapshot> ReadGraphicsStatusAsync(bool allowElevation, CancellationToken ct = default)
    {
        if (LenovoWmiProvider.IsProcessElevated() || !allowElevation)
        {
            return await Task.Run(_wmi.ReadGraphicsStatusDirect, ct).ConfigureAwait(false);
        }

        var resp = await _elevated.ExecuteRequestAsync(new ElevatedRequest
        {
            AuthToken = string.Empty,
            Operation = ElevatedOperation.ReadGraphicsMode
        }, ct).ConfigureAwait(false);

        if (resp.Success && resp.GraphicsStatus is not null)
        {
            return resp.GraphicsStatus;
        }

        var fallback = await Task.Run(_wmi.ReadGraphicsStatusDirect, ct).ConfigureAwait(false);
        return fallback with { StatusNote = resp.WasCancelled ? "USER_CANCELLED" : resp.Message ?? fallback.StatusNote };
    }

    public async Task<OperationResult<GraphicsStatusSnapshot>> SetGraphicsModeAsync(GraphicsRoutingMode mode, CancellationToken ct = default)
    {
        if (LenovoWmiProvider.IsProcessElevated())
            return await Task.Run(() => _wmi.SetGraphicsModeDirect(mode), ct).ConfigureAwait(false);

        var response = await _elevated.ExecuteRequestAsync(new ElevatedRequest
        {
            AuthToken = string.Empty,
            Operation = ElevatedOperation.SetGraphicsMode,
            GraphicsMode = mode
        }, ct).ConfigureAwait(false);

        if (response.WasCancelled)
            return OperationResult<GraphicsStatusSnapshot>.Cancelled(response.Message ?? "Cancelled.");
        return response.Success && response.GraphicsStatus is { } snapshot
            ? OperationResult<GraphicsStatusSnapshot>.Ok(snapshot)
            : OperationResult<GraphicsStatusSnapshot>.Fail(response.ErrorCode ?? "WORKER_UNVERIFIED", response.Message ?? "Graphics switch unverified.");
    }

    private static Task<OperationResult<T>> WritePendingAsync<T>(string operation) =>
        Task.FromResult(OperationResult<T>.Fail("WRITE_NOT_VALIDATED",
            $"{operation} require target firmware validation before use. No changes were made."));

    private static FeatureCapability ReadOnlyCapability(
        FeatureId feature,
        FeatureCapability source,
        MachineIdentity machine,
        string provider,
        string note) =>
        source with
        {
            Feature = feature,
            WriteAvailable = false,
            Status = !machine.IsTargetLegion83JJ ? CapabilityAvailability.UnsupportedMachine
                : source.ReadAvailable ? CapabilityAvailability.PendingValidation : source.Status,
            Reason = source.ReadAvailable ? note : $"{source.Reason} {note}",
            Provider = provider
        };

}
