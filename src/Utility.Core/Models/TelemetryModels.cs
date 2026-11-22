namespace Utility.Core.Models;

/// <summary>Distinguishes genuine zero measurements from missing, stale, or failed readings.</summary>
public enum SensorStatus
{
    Valid = 0,
    Unavailable = 1,
    Stale = 2,
    Error = 3,
    Sleeping = 4,
    PausedOnBattery = 5
}

/// <summary>Typed sensor value wrapper carrying explicit availability and provenance metadata.</summary>
public sealed record SensorReading<T>(
    T? Value,
    string Unit,
    SensorStatus Status,
    string Source,
    DateTimeOffset TimestampUtc,
    string? Note = null) where T : struct;

/// <summary>Logical fixed disk capacity usage separate from I/O activity.</summary>
public sealed record DiskCapacityInfo(
    string DriveLetter,
    string VolumeLabel,
    long TotalBytes,
    long UsedBytes,
    long FreeBytes)
{
    public double TotalGb => TotalBytes / (1024d * 1024d * 1024d);
    public double UsedGb => UsedBytes / (1024d * 1024d * 1024d);
    public double UsedPercent => TotalBytes > 0 ? UsedBytes * 100d / TotalBytes : 0d;
}

/// <summary>Hardware telemetry snapshot for F3 monitoring.</summary>
public sealed record TelemetrySnapshot(
    SensorReading<double> CpuUtilization,
    SensorReading<double> CpuTemperature,
    SensorReading<double> MemoryUsedGb,
    SensorReading<double> MemoryTotalGb,
    SensorReading<double> MemoryUsedPercent,
    SensorReading<double> DiskActivePercent,
    SensorReading<double> DiskThroughputMbPerSec,
    IReadOnlyList<DiskCapacityInfo> DiskCapacities,
    string? GpuName,
    SensorReading<double> GpuUtilization,
    SensorReading<double> GpuTemperature,
    SensorReading<double> GpuPowerWatts,
    SensorReading<double> GpuMemoryUsedMb,
    SensorReading<double> GpuMemoryTotalMb,
    string? GpuPowerState,
    SensorReading<int> CpuFanRpm,
    SensorReading<int> GpuFanRpm,
    bool IsOnBattery,
    DateTimeOffset CapturedAtUtc);
