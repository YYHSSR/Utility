namespace Utility.Core.Models;

/// <summary>Closed set of typed operations allowed across the elevated worker boundary.</summary>
public enum ElevatedOperation
{
    ProbeCapabilities = 1,
    ReadThermalSnapshot = 2,
    SetThermalMode = 3,
    ApplyPowerFields = 4,
    ReadFanTables = 5,
    ApplyFanTables = 6,
    RestoreDefaultFanTables = 7,
    ReadGraphicsMode = 8,
    SetGraphicsMode = 9,
    ReadBatteryConservation = 10,
    SetBatteryConservation = 11
}

/// <summary>Typed IPC request sent over an ACL-restricted named pipe to Utility.ElevatedWorker.</summary>
public sealed record ElevatedRequest
{
    public required string AuthToken { get; init; }
    public required ElevatedOperation Operation { get; init; }
    public ThermalMode? ThermalMode { get; init; }
    public ProfilePowerSettings? PowerSettings { get; init; }
    public FanCurveProfile? FanCurve { get; init; }
    public GraphicsRoutingMode? GraphicsMode { get; init; }
    public bool? ConservationEnabled { get; init; }
}

/// <summary>Typed IPC response returned by Utility.ElevatedWorker.</summary>
public sealed record ElevatedResponse
{
    public bool Success { get; init; }
    public bool WasCancelled { get; init; }
    public string? ErrorCode { get; init; }
    public string? Message { get; init; }
    public CapabilityReport? CapabilityReport { get; init; }
    public LiveThermalSnapshot? ThermalSnapshot { get; init; }
    public FanCurveProfile? FanCurve { get; init; }
    public GraphicsStatusSnapshot? GraphicsStatus { get; init; }
    public BatteryChargeMode? BatteryChargeMode { get; init; }
    public IReadOnlyDictionary<string, int?>? ReadBackPowerValues { get; init; }
    public IReadOnlyList<string> AppliedFields { get; init; } = [];
    public IReadOnlyList<string> FailedFields { get; init; } = [];
}
