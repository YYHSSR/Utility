using System.Diagnostics;
using System.Globalization;
using Utility.Core.Models;
using Utility.Core.Services;
using Utility.Platform.Native;

namespace Utility.Platform.Providers;

/// <summary>
/// Provides Windows CPU, RAM, disk capacity/activity, NVIDIA RTX telemetry via trusted nvidia-smi,
/// and distinguishes 0 readings from unavailable, stale, or battery-paused sensors.
/// </summary>
public sealed class WindowsTelemetryProvider : ITelemetryProvider, IDisposable
{
    private SystemMetricsInterop.CpuTimeSample? _lastCpuSample;
    private PerformanceCounter? _diskActiveCounter;
    private PerformanceCounter? _diskBytesCounter;
    private bool _countersInitialized;

    public async Task<TelemetrySnapshot> ReadSnapshotAsync(bool allowWakeDgpu, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var isOnBattery = !Win32BatteryInterop.IsAcOnlineQuick();

        // CPU utilization via GetSystemTimes delta.
        var currentCpu = SystemMetricsInterop.SampleCpuTimes();
        SensorReading<double> cpuUtil;
        if (currentCpu is not null && _lastCpuSample is not null)
        {
            var pct = SystemMetricsInterop.ComputeCpuUtilization(_lastCpuSample, currentCpu);
            cpuUtil = pct is not null
                ? new SensorReading<double>(Math.Round(pct.Value, 1), "%", SensorStatus.Valid, "Win32 GetSystemTimes", now)
                : new SensorReading<double>(null, "%", SensorStatus.Unavailable, "Win32 GetSystemTimes", now, "Zero delta interval");
        }
        else
        {
            cpuUtil = new SensorReading<double>(null, "%", SensorStatus.Stale, "Win32 GetSystemTimes", now, "Warming up CPU baseline sample");
        }
        _lastCpuSample = currentCpu;

        // CPU package temperature is not exposed cleanly to standard users via documented Windows APIs;
        // never mislabel generic ACPI thermal zone as CPU package temperature.
        var cpuTemp = new SensorReading<double>(
            null,
            "C",
            SensorStatus.Unavailable,
            "Unavailable (unprivileged)",
            now,
            "ACPI thermal zone excluded; requires verified CPU package telemetry.");

        // Physical memory usage.
        var mem = SystemMetricsInterop.ReadMemoryUsage();
        var memUsed = mem is not null
            ? new SensorReading<double>(Math.Round(mem.Value.UsedGb, 2), "GB", SensorStatus.Valid, "Win32 GlobalMemoryStatusEx", now)
            : new SensorReading<double>(null, "GB", SensorStatus.Unavailable, "Win32 GlobalMemoryStatusEx", now);
        var memTotal = mem is not null
            ? new SensorReading<double>(Math.Round(mem.Value.TotalGb, 2), "GB", SensorStatus.Valid, "Win32 GlobalMemoryStatusEx", now)
            : new SensorReading<double>(null, "GB", SensorStatus.Unavailable, "Win32 GlobalMemoryStatusEx", now);
        var memPct = mem is not null
            ? new SensorReading<double>(Math.Round(mem.Value.UsedPercent, 1), "%", SensorStatus.Valid, "Win32 GlobalMemoryStatusEx", now)
            : new SensorReading<double>(null, "%", SensorStatus.Unavailable, "Win32 GlobalMemoryStatusEx", now);

        // Disk capacity (separate from disk activity).
        var disks = ReadDiskCapacities();
        var (diskPct, diskThroughput) = ReadDiskActivity(now);

        // NVIDIA RTX telemetry via trusted nvidia-smi.exe.
        var gpu = await ReadNvidiaTelemetryAsync(isOnBattery, allowWakeDgpu, now, ct).ConfigureAwait(false);

        // Lenovo fan RPM requires elevated WMI access; report Unavailable rather than guessing 0 RPM.
        var fanUnavailable = new SensorReading<int>(
            null,
            "RPM",
            SensorStatus.Unavailable,
            "LENOVO_GAMEZONE_DATA",
            now,
            "Requires elevated Lenovo WMI read.");

        return new TelemetrySnapshot(
            CpuUtilization: cpuUtil,
            CpuTemperature: cpuTemp,
            MemoryUsedGb: memUsed,
            MemoryTotalGb: memTotal,
            MemoryUsedPercent: memPct,
            DiskActivePercent: diskPct,
            DiskThroughputMbPerSec: diskThroughput,
            DiskCapacities: disks,
            GpuName: gpu.Name,
            GpuUtilization: gpu.Utilization,
            GpuTemperature: gpu.Temperature,
            GpuPowerWatts: gpu.PowerWatts,
            GpuMemoryUsedMb: gpu.MemoryUsedMb,
            GpuMemoryTotalMb: gpu.MemoryTotalMb,
            GpuPowerState: gpu.PowerState,
            CpuFanRpm: fanUnavailable,
            GpuFanRpm: fanUnavailable,
            IsOnBattery: isOnBattery,
            CapturedAtUtc: now);
    }

    public Task<FeatureCapability> ProbeCapabilityAsync(CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var now = DateTimeOffset.UtcNow;
            var smiPath = FindTrustedNvidiaSmiPath();
            var details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["CpuMemorySource"] = "Win32 GetSystemTimes + GlobalMemoryStatusEx",
                ["DiskSource"] = "DriveInfo + PhysicalDisk PerformanceCounter",
                ["NvidiaSmiPath"] = smiPath ?? "NotFound",
                ["CpuTempAndFanRpm"] = "Unavailable in standard-user mode (requires elevated WMI or verified sensor)"
            };

            return new FeatureCapability(
                FeatureId.HardwareMonitoring,
                ReadAvailable: true,
                WriteAvailable: false,
                Status: CapabilityAvailability.Available,
                Reason: smiPath is not null
                    ? "System metrics and NVIDIA telemetry are available; fan RPM and CPU package temp require verified elevated/sensor access."
                    : "System metrics available; nvidia-smi not found.",
                Provider: "Win32 + PerformanceCounter + nvidia-smi",
                LastCheckedUtc: now,
                Details: details);
        }, ct);
    }

    private (SensorReading<double> ActivePct, SensorReading<double> ThroughputMbPerSec) ReadDiskActivity(DateTimeOffset now)
    {
        try
        {
            if (!_countersInitialized)
            {
                _countersInitialized = true;
                _diskActiveCounter = new PerformanceCounter("PhysicalDisk", "% Disk Time", "_Total", readOnly: true);
                _diskBytesCounter = new PerformanceCounter("PhysicalDisk", "Disk Bytes/sec", "_Total", readOnly: true);
                _ = _diskActiveCounter.NextValue();
                _ = _diskBytesCounter.NextValue();
                return (
                    new SensorReading<double>(null, "%", SensorStatus.Stale, "PerformanceCounter PhysicalDisk", now, "Warming up counter"),
                    new SensorReading<double>(null, "MB/s", SensorStatus.Stale, "PerformanceCounter PhysicalDisk", now, "Warming up counter"));
            }

            if (_diskActiveCounter is null || _diskBytesCounter is null)
            {
                return (
                    new SensorReading<double>(null, "%", SensorStatus.Unavailable, "PerformanceCounter PhysicalDisk", now),
                    new SensorReading<double>(null, "MB/s", SensorStatus.Unavailable, "PerformanceCounter PhysicalDisk", now));
            }

            var pct = Math.Clamp(_diskActiveCounter.NextValue(), 0f, 100f);
            var mbps = Math.Max(0f, _diskBytesCounter.NextValue()) / (1024.0 * 1024.0);
            return (
                new SensorReading<double>(Math.Round(pct, 1), "%", SensorStatus.Valid, "PerformanceCounter PhysicalDisk", now),
                new SensorReading<double>(Math.Round(mbps, 2), "MB/s", SensorStatus.Valid, "PerformanceCounter PhysicalDisk", now));
        }
        catch (Exception ex)
        {
            return (
                new SensorReading<double>(null, "%", SensorStatus.Error, "PerformanceCounter PhysicalDisk", now, ex.Message),
                new SensorReading<double>(null, "MB/s", SensorStatus.Error, "PerformanceCounter PhysicalDisk", now, ex.Message));
        }
    }

    private static IReadOnlyList<DiskCapacityInfo> ReadDiskCapacities()
    {
        var list = new List<DiskCapacityInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
            {
                continue;
            }

            var total = drive.TotalSize;
            var free = drive.AvailableFreeSpace;
            var used = Math.Max(0L, total - free);
            list.Add(new DiskCapacityInfo(drive.Name.TrimEnd('\\'), drive.VolumeLabel, total, used, free));
        }

        return list;
    }

    private static async Task<(
        string? Name,
        SensorReading<double> Utilization,
        SensorReading<double> Temperature,
        SensorReading<double> PowerWatts,
        SensorReading<double> MemoryUsedMb,
        SensorReading<double> MemoryTotalMb,
        string? PowerState)> ReadNvidiaTelemetryAsync(
        bool isOnBattery,
        bool allowWakeDgpu,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (isOnBattery && !allowWakeDgpu)
        {
            var paused = new SensorReading<double>(null, string.Empty, SensorStatus.PausedOnBattery, "nvidia-smi", now, "Paused on battery to avoid waking dGPU; click Manual Refresh to query.");
            return (null, paused with { Unit = "%" }, paused with { Unit = "C" }, paused with { Unit = "W" }, paused with { Unit = "MB" }, paused with { Unit = "MB" }, null);
        }

        var smiPath = FindTrustedNvidiaSmiPath();
        if (smiPath is null)
        {
            var unavail = new SensorReading<double>(null, string.Empty, SensorStatus.Unavailable, "nvidia-smi", now, "Trusted nvidia-smi executable not found.");
            return (null, unavail with { Unit = "%" }, unavail with { Unit = "C" }, unavail with { Unit = "W" }, unavail with { Unit = "MB" }, unavail with { Unit = "MB" }, null);
        }

        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = smiPath,
                    Arguments = "--query-gpu=name,utilization.gpu,temperature.gpu,power.draw,memory.used,memory.total,pstate --format=csv,noheader,nounits",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            if (!proc.Start())
            {
                var err = new SensorReading<double>(null, string.Empty, SensorStatus.Error, "nvidia-smi", now, "Failed to start nvidia-smi.");
                return (null, err with { Unit = "%" }, err with { Unit = "C" }, err with { Unit = "W" }, err with { Unit = "MB" }, err with { Unit = "MB" }, null);
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));

            var outputTask = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var errTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);
            await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            var stdout = (await outputTask.ConfigureAwait(false)).Trim();
            var stderr = (await errTask.ConfigureAwait(false)).Trim();

            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            {
                var status = stderr.Contains("No devices", StringComparison.OrdinalIgnoreCase)
                    ? SensorStatus.Unavailable
                    : SensorStatus.Error;
                var msg = string.IsNullOrWhiteSpace(stderr) ? $"nvidia-smi exited with code {proc.ExitCode}" : stderr;
                var reading = new SensorReading<double>(null, string.Empty, status, "nvidia-smi", now, msg);
                return (null, reading with { Unit = "%" }, reading with { Unit = "C" }, reading with { Unit = "W" }, reading with { Unit = "MB" }, reading with { Unit = "MB" }, null);
            }

            var firstLine = stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[0];
            var cols = firstLine.Split(',').Select(s => s.Trim()).ToArray();
            if (cols.Length < 7)
            {
                var err = new SensorReading<double>(null, string.Empty, SensorStatus.Error, "nvidia-smi", now, "Unexpected CSV column count.");
                return (null, err with { Unit = "%" }, err with { Unit = "C" }, err with { Unit = "W" }, err with { Unit = "MB" }, err with { Unit = "MB" }, null);
            }

            return (
                cols[0],
                ParseNvidiaMetric(cols[1], "%", now),
                ParseNvidiaMetric(cols[2], "C", now),
                ParseNvidiaMetric(cols[3], "W", now),
                ParseNvidiaMetric(cols[4], "MB", now),
                ParseNvidiaMetric(cols[5], "MB", now),
                cols[6]);
        }
        catch (OperationCanceledException)
        {
            var stale = new SensorReading<double>(null, string.Empty, SensorStatus.Stale, "nvidia-smi", now, "nvidia-smi query timed out.");
            return (null, stale with { Unit = "%" }, stale with { Unit = "C" }, stale with { Unit = "W" }, stale with { Unit = "MB" }, stale with { Unit = "MB" }, null);
        }
        catch (Exception ex)
        {
            var err = new SensorReading<double>(null, string.Empty, SensorStatus.Error, "nvidia-smi", now, ex.Message);
            return (null, err with { Unit = "%" }, err with { Unit = "C" }, err with { Unit = "W" }, err with { Unit = "MB" }, err with { Unit = "MB" }, null);
        }
    }

    private static SensorReading<double> ParseNvidiaMetric(string raw, string unit, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Contains("N/A", StringComparison.OrdinalIgnoreCase) || raw.Contains("Not Supported", StringComparison.OrdinalIgnoreCase))
        {
            return new SensorReading<double>(null, unit, SensorStatus.Unavailable, "nvidia-smi", now, raw);
        }

        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var val))
        {
            return new SensorReading<double>(val, unit, SensorStatus.Valid, "nvidia-smi", now);
        }

        return new SensorReading<double>(null, unit, SensorStatus.Error, "nvidia-smi", now, $"Unparseable value: {raw}");
    }

    private static string? FindTrustedNvidiaSmiPath()
    {
        var sysPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe");
        if (File.Exists(sysPath))
        {
            return sysPath;
        }

        var pfPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe");
        return File.Exists(pfPath) ? pfPath : null;
    }

    public void Dispose()
    {
        _diskActiveCounter?.Dispose();
        _diskBytesCounter?.Dispose();
    }
}
