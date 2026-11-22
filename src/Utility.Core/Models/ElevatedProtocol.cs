namespace Utility.Core.Models;

/// <summary>Closed set of typed operations allowed across the elevated worker boundary.</summary>
public enum ElevatedOperation
{
    ProbeCapabilities = 1,
    ReadThermalSnapshot = 2,
    ReadFanTables = 5,
    ReadGraphicsMode = 8,
    ApplyOwnerPowerPreset = 12,
    RunCoolingCycle = 13,
    MonitorFirmwareSensors = 14,
    SetGraphicsMode = 15,
    SetFanSpeedLevels = 16,
    ListRestorePoints = 17,
    DeleteRestorePoints = 18,
    SetStartupEntryEnabled = 19
}

/// <summary>One Windows System Restore point (rootdefault:SystemRestore).</summary>
public sealed record RestorePointInfo(uint SequenceNumber, string Description, DateTime CreatedUtc);

/// <summary>Restore points plus the shadow-copy space they use on the system drive (null when unknown).</summary>
public sealed record RestorePointList(IReadOnlyList<RestorePointInfo> Points, long? UsedBytes);

public enum CoolingStopReason { TemperatureReached, TimeLimit, Stopped, AcDisconnected, SensorUnavailable }

public sealed record CoolingTemperatures(int? CpuC, int? GpuC);

public sealed record CoolingCycleResult(CoolingTemperatures Start, CoolingTemperatures End, CoolingStopReason Reason);

/// <summary>Typed IPC request with an optional owner profile ID for the verified power switch.</summary>
public sealed record ElevatedRequest
{
    public required string AuthToken { get; init; }
    public required ElevatedOperation Operation { get; init; }
    public string? OwnerProfileId { get; init; }
    public GraphicsRoutingMode? GraphicsMode { get; init; }
    public int[]? FanLevels { get; init; }
    public uint[]? RestorePointSequenceNumbers { get; init; }
    /// <summary>StableId of an existing startup entry (re-enumerated by the worker, never a raw path).</summary>
    public string? StartupEntryId { get; init; }
    public bool? StartupEnabled { get; init; }
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
    public CoolingCycleResult? CoolingCycle { get; init; }
    public FirmwareSensorSample? FirmwareSensors { get; init; }
    public RestorePointList? RestorePoints { get; init; }
}
