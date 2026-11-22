using Utility.Core.Models;

namespace Utility.Core.Services;

/// <summary>Battery telemetry and Lenovo conservation policy provider.</summary>
public interface IBatteryProvider
{
    Task<BatteryStatusSnapshot> ReadStatusAsync(CancellationToken ct = default);
    Task<FeatureCapability> ProbeCapabilityAsync(CancellationToken ct = default);
    Task<OperationResult<BatteryChargeMode>> SetConservationModeAsync(bool enabled, CancellationToken ct = default);
}

/// <summary>Live CPU, RAM, disk, NVIDIA GPU, and fan telemetry provider.</summary>
public interface ITelemetryProvider
{
    Task<TelemetrySnapshot> ReadSnapshotAsync(bool allowWakeDgpu, CancellationToken ct = default);
    Task<FeatureCapability> ProbeCapabilityAsync(CancellationToken ct = default);
}

/// <summary>Read-only Windows software, startup, and Lenovo leftover provider.</summary>
public interface ISoftwareInventoryProvider
{
    Task<SoftwareInventorySnapshot> ScanAsync(bool includeLenovoLeftovers, CancellationToken ct = default);
    bool OpenWindowsInstalledApps(string? packageFamilyName = null);
    bool OpenWindowsStartupApps();
}

/// <summary>PnP driver inventory and local official driver package verifier.</summary>
public interface IDriverProvider
{
    Task<IReadOnlyList<InstalledDriverEntry>> ListDriversAsync(CancellationToken ct = default);
    Task<DriverPackageInspection> InspectLocalPackageAsync(string filePath, CancellationToken ct = default);
    Task<OperationResult<int>> LaunchVerifiedInstallerAsync(string filePath, string expectedSha256Hex, CancellationToken ct = default);
    bool OpenExternalTarget(string urlOrSettingsUri);
}

/// <summary>Lenovo hardware control, WMI capability probe, and Windows power mode provider.</summary>
public interface ILenovoHardwareControlProvider
{
    Task<MachineIdentity> GetMachineIdentityAsync(CancellationToken ct = default);
    Task<CapabilityReport> ProbeCapabilitiesAsync(bool allowElevation, CancellationToken ct = default);
    Task<LiveThermalSnapshot> ReadLiveThermalSnapshotAsync(bool allowElevation, CancellationToken ct = default);
    Task<OperationResult<ThermalMode>> SetThermalModeAsync(ThermalMode mode, CancellationToken ct = default);
    Task<OperationResult<WindowsPowerMode>> SetWindowsPowerModeAsync(WindowsPowerMode mode, CancellationToken ct = default);
    Task<OperationResult<IReadOnlyDictionary<string, int?>>> ApplyPowerFieldsAsync(
        IReadOnlyDictionary<string, int> backendValuesByField,
        IReadOnlyList<FieldHardwareContract> contracts,
        CancellationToken ct = default);
    Task<OperationResult<FanCurveProfile>> ReadFanCurveAsync(bool allowElevation, CancellationToken ct = default);
    Task<OperationResult<FanCurveProfile>> ApplyFanCurveAsync(FanCurveProfile curve, CancellationToken ct = default);
    Task<OperationResult<FanCurveProfile>> RestoreDefaultFanCurveAsync(CancellationToken ct = default);
    Task<GraphicsStatusSnapshot> ReadGraphicsStatusAsync(bool allowElevation, CancellationToken ct = default);
    Task<OperationResult<GraphicsStatusSnapshot>> SetGraphicsModeAsync(GraphicsRoutingMode mode, CancellationToken ct = default);
}

/// <summary>Typed result wrapper distinguishing elevation cancellation and hardware error codes.</summary>
public sealed record OperationResult<T>(
    bool Success,
    T? Value,
    bool WasCancelledByUser,
    string ErrorCode,
    string Message,
    IReadOnlyList<string>? FailedFields = null)
{
    public static OperationResult<T> Ok(T value, string message = "OK") =>
        new(true, value, false, "OK", message);

    public static OperationResult<T> Cancelled(string message = "Elevation or confirmation was cancelled by the user.") =>
        new(false, default, true, "USER_CANCELLED", message);

    public static OperationResult<T> Fail(string errorCode, string message, IReadOnlyList<string>? failedFields = null) =>
        new(false, default, false, errorCode, message, failedFields);
}
