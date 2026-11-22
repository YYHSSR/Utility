namespace Utility.Core.Models;

/// <summary>Identifies a functional module or sub-capability probed on the host machine.</summary>
public enum FeatureId
{
    PerformanceProfiles,
    BatteryConservation,
    HardwareMonitoring,
    GraphicsMode,
    SoftwareAndStartup,
    CustomFanCurves,
    OfficialDrivers,
    LenovoWmiGamezone,
    LenovoWmiOther,
    LenovoWmiFan
}

/// <summary>Categorizes why a feature is ready or blocked on the current machine.</summary>
public enum CapabilityAvailability
{
    Available,
    UnsupportedMachine,
    UnavailableInterface,
    InsufficientPrivilege,
    TransientFailure,
    RebootPending,
    PendingValidation
}

/// <summary>Per-feature capability probe result with read/write states and provenance.</summary>
public sealed record FeatureCapability(
    FeatureId Feature,
    bool ReadAvailable,
    bool WriteAvailable,
    CapabilityAvailability Status,
    string Reason,
    string Provider,
    DateTimeOffset LastCheckedUtc,
    IReadOnlyDictionary<string, string>? Details = null);

/// <summary>Hardware and OS identity observed directly from Windows.</summary>
public sealed record MachineIdentity(
    string Manufacturer,
    string Model,
    string SystemFamily,
    string SystemSku,
    string BiosVersion,
    string OsBuild)
{
    public bool IsTargetLegion83JJ =>
        string.Equals(Manufacturer?.Trim(), "LENOVO", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Model?.Trim(), "83JJ", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Aggregated capability snapshot generated from standard or elevated probing.</summary>
public sealed record CapabilityReport(
    DateTimeOffset TimestampUtc,
    bool IsElevated,
    MachineIdentity Machine,
    IReadOnlyList<FeatureCapability> Capabilities,
    IReadOnlyList<string> UnresolvedItems);
