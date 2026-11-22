using Utility.Core.Models;
using Utility.Core.Services;

namespace Utility.Tests;

internal sealed class SimulatedLenovoHardwareProvider : ILenovoHardwareControlProvider
{
    public MachineIdentity Machine { get; set; } = new("LENOVO", "83JJ", "Legion Y7000 IRX9", "83JJ", "PTCN18WW", "26300");
    public ThermalMode CurrentThermalMode { get; set; } = ThermalMode.Balanced;
    public WindowsPowerMode CurrentWindowsPowerMode { get; set; } = WindowsPowerMode.Balanced;
    public bool IsOnAcPower { get; set; } = true;
    public ProfilePowerSettings CurrentPowerValues { get; set; } = ProfileSeedFactory.CreatePristineReference(ProfileSeedFactory.DailyId).Power;
    public GraphicsRoutingMode CurrentGraphicsMode { get; set; } = GraphicsRoutingMode.Hybrid;

    public int WriteCallsCount { get; private set; }
    public bool SimulateUserCancelled { get; set; }
    public bool FailNextPowerWrite { get; set; }
    public bool ReturnMismatchedReadBack { get; set; }

    public Task<MachineIdentity> GetMachineIdentityAsync(CancellationToken ct = default) => Task.FromResult(Machine);

    public Task<CapabilityReport> ProbeCapabilitiesAsync(bool allowElevation, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var caps = new[]
        {
            new FeatureCapability(FeatureId.PerformanceProfiles, allowElevation, allowElevation,
                allowElevation ? CapabilityAvailability.Available : CapabilityAvailability.InsufficientPrivilege,
                allowElevation ? "Simulated available" : "Simulated standard user", "Simulated", now)
        };
        return Task.FromResult(new CapabilityReport(now, allowElevation, Machine, caps, []));
    }

    public Task<LiveThermalSnapshot> ReadLiveThermalSnapshotAsync(bool allowElevation, CancellationToken ct = default)
    {
        return Task.FromResult(new LiveThermalSnapshot(
            CurrentThermalMode,
            CurrentWindowsPowerMode,
            "Balanced",
            IsOnAcPower,
            CurrentPowerValues,
            null,
            true,
            "Simulated live read",
            DateTimeOffset.UtcNow));
    }

    public Task<OperationResult<ThermalMode>> SetThermalModeAsync(ThermalMode mode, CancellationToken ct = default)
    {
        if (SimulateUserCancelled)
        {
            return Task.FromResult(OperationResult<ThermalMode>.Cancelled("Simulated UAC cancel."));
        }

        WriteCallsCount++;
        CurrentThermalMode = mode;
        return Task.FromResult(OperationResult<ThermalMode>.Ok(mode));
    }

    public Task<OperationResult<WindowsPowerMode>> SetWindowsPowerModeAsync(WindowsPowerMode mode, CancellationToken ct = default)
    {
        CurrentWindowsPowerMode = mode;
        return Task.FromResult(OperationResult<WindowsPowerMode>.Ok(mode));
    }

    public Task<OperationResult<IReadOnlyDictionary<string, int?>>> ApplyPowerFieldsAsync(
        IReadOnlyDictionary<string, int> backendValuesByField,
        IReadOnlyList<FieldHardwareContract> contracts,
        CancellationToken ct = default)
    {
        if (SimulateUserCancelled)
        {
            return Task.FromResult(OperationResult<IReadOnlyDictionary<string, int?>>.Cancelled("Simulated UAC cancel."));
        }

        WriteCallsCount++;
        if (FailNextPowerWrite)
        {
            FailNextPowerWrite = false;
            return Task.FromResult(OperationResult<IReadOnlyDictionary<string, int?>>.Fail(
                "SIMULATED_WRITE_ERROR",
                "Simulated mid-operation failure on GpuConfigurableTgpW.",
                ["GpuConfigurableTgpW"]));
        }

        int Decode(string key) => ProfileValidationService.FromBackendValue(
            backendValuesByField[key],
            contracts.First(c => c.FieldKey == key));

        CurrentPowerValues = new ProfilePowerSettings
        {
            CpuLongTermPowerLimitW = Decode(nameof(ProfilePowerSettings.CpuLongTermPowerLimitW)),
            CpuCrossLoadingPowerLimitW = Decode(nameof(ProfilePowerSettings.CpuCrossLoadingPowerLimitW)),
            CpuLongTermPowerLimitTau = Decode(nameof(ProfilePowerSettings.CpuLongTermPowerLimitTau)),
            CpuShortTermPowerLimitW = Decode(nameof(ProfilePowerSettings.CpuShortTermPowerLimitW)),
            CpuThermalControlCircuitC = ReturnMismatchedReadBack ? 99 : Decode(nameof(ProfilePowerSettings.CpuThermalControlCircuitC)),
            GpuDynamicBoostW = Decode(nameof(ProfilePowerSettings.GpuDynamicBoostW)),
            GpuToCpuDynamicBoostW = Decode(nameof(ProfilePowerSettings.GpuToCpuDynamicBoostW)),
            GpuConfigurableTgpW = Decode(nameof(ProfilePowerSettings.GpuConfigurableTgpW)),
            GpuTargetTemperatureLimitC = Decode(nameof(ProfilePowerSettings.GpuTargetTemperatureLimitC)),
            TotalProcessorPowerTargetAcW = Decode(nameof(ProfilePowerSettings.TotalProcessorPowerTargetAcW))
        };

        return Task.FromResult(OperationResult<IReadOnlyDictionary<string, int?>>.Ok(CurrentPowerValues.AsFieldMap()));
    }

    public Task<OperationResult<FanCurveProfile>> ReadFanCurveAsync(bool allowElevation, CancellationToken ct = default) =>
        Task.FromResult(OperationResult<FanCurveProfile>.Ok(new FanCurveProfile(FanCurveStatus.PendingCapture, "Simulated", [])));

    public Task<OperationResult<FanCurveProfile>> ApplyFanCurveAsync(FanCurveProfile curve, CancellationToken ct = default) =>
        Task.FromResult(OperationResult<FanCurveProfile>.Ok(curve));

    public Task<OperationResult<FanCurveProfile>> RestoreDefaultFanCurveAsync(CancellationToken ct = default) =>
        Task.FromResult(OperationResult<FanCurveProfile>.Ok(new FanCurveProfile(FanCurveStatus.Verified, "Default", [])));

    public Task<GraphicsStatusSnapshot> ReadGraphicsStatusAsync(bool allowElevation, CancellationToken ct = default) =>
        Task.FromResult(new GraphicsStatusSnapshot(CurrentGraphicsMode, null, false, [], true, "Simulated", DateTimeOffset.UtcNow));

    public Task<OperationResult<GraphicsStatusSnapshot>> SetGraphicsModeAsync(GraphicsRoutingMode mode, CancellationToken ct = default)
    {
        CurrentGraphicsMode = mode;
        return Task.FromResult(OperationResult<GraphicsStatusSnapshot>.Ok(
            new GraphicsStatusSnapshot(mode, mode, true, [], true, "Reboot required", DateTimeOffset.UtcNow)));
    }
}
