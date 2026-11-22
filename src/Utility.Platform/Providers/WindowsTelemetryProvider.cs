using System.Diagnostics;
using System.Globalization;
using Utility.Core.Models;
using Utility.Core.Services;
using Utility.Platform.Lenovo;
using Utility.Platform.Native;

namespace Utility.Platform.Providers;

/// <summary>
/// Provides Windows CPU, RAM, disk capacity/activity, NVIDIA RTX telemetry via trusted nvidia-smi,
/// and distinguishes 0 readings from unavailable, stale, or battery-paused sensors.
/// </summary>
public sealed class WindowsTelemetryProvider : ITelemetryProvider, IDisposable
{
    private SystemMetricsInterop.CpuTimeSample? _lastCpuSample = SystemMetricsInterop.SampleCpuTimes();
    private readonly PerformanceQuery _counters = new();
    private readonly NvidiaTelemetry _nvidia = new();
    private readonly object _nativeLock = new();
    private bool _disposed;
    private IReadOnlyList<DiskCapacityInfo> _diskCapacities = [];
    private DateTimeOffset _nextDiskCapacityRefresh;
    private DateTimeOffset _nextNvmlAttempt;
    private readonly bool _elevated = LenovoWmiProvider.IsProcessElevated();

    public async Task<TelemetrySnapshot> ReadSnapshotAsync(bool allowWakeDgpu, CancellationToken ct = default, bool pauseDgpu = false)
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

        // CPU temperature and fan RPM come from Lenovo firmware WMI (LENOVO_OTHER_METHOD), which Windows
        // only exposes to administrators. Standard users get RequiresElevation, never a guessed value;
        // the generic ACPI thermal zone is not mislabelled as CPU temperature.
        // The WMI call runs off the UI thread (this method is entered from a dispatcher timer).
        var elevated = _elevated;
        var firmware = elevated
            ? await Task.Run(LenovoWmiProvider.TryReadFirmwareSensors, ct).ConfigureAwait(false)
            : null;
        const string firmwareSource = "LENOVO_OTHER_METHOD";
        var missingStatus = elevated ? SensorStatus.Unavailable : SensorStatus.RequiresElevation;
        var cpuTemp = firmware?.CpuTemperatureC is int cpuC
            ? new SensorReading<double>(cpuC, "C", SensorStatus.Valid, firmwareSource, now)
            : new SensorReading<double>(null, "C", missingStatus, firmwareSource, now,
                elevated ? "Firmware returned no valid CPU temperature." : "Requires administrator rights.");

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
        if (now >= _nextDiskCapacityRefresh)
        {
            _diskCapacities = ReadDiskCapacities();
            _nextDiskCapacityRefresh = now.AddSeconds(30);
        }
        (double? DiskTime, double? DiskBytes, double? PackageMilliwatts) counters;
        lock (_nativeLock)
            counters = _disposed ? default : _counters.Read();
        SensorReading<double> Counter(double? value, string unit, string source) =>
            new(value, unit, value.HasValue ? SensorStatus.Valid : SensorStatus.Unavailable, source, now);
        var diskPct = Counter(counters.DiskTime is double active ? Math.Clamp(active, 0, 100) : null, "%", "PDH PhysicalDisk");
        var diskThroughput = Counter(counters.DiskBytes / 1048576d, "MB/s", "PDH PhysicalDisk");

        // NVIDIA RTX telemetry via trusted nvidia-smi.exe.
        var gpu = await ReadNvidiaTelemetryAsync(isOnBattery, allowWakeDgpu, pauseDgpu, now, ct).ConfigureAwait(false);

        // Fan RPM: same firmware source and privilege rule as CPU temperature; never guess 0 RPM.
        SensorReading<int> Fan(int? rpm) => rpm is int value
            ? new SensorReading<int>(value, "RPM", SensorStatus.Valid, firmwareSource, now)
            : new SensorReading<int>(null, "RPM", missingStatus, firmwareSource, now,
                elevated ? "Firmware returned no valid fan speed." : "Requires administrator rights.");

        return new TelemetrySnapshot(
            CpuUtilization: cpuUtil,
            CpuTemperature: cpuTemp,
            MemoryUsedGb: memUsed,
            MemoryTotalGb: memTotal,
            MemoryUsedPercent: memPct,
            DiskActivePercent: diskPct,
            DiskThroughputMbPerSec: diskThroughput,
            DiskCapacities: _diskCapacities,
            GpuName: gpu.Name,
            GpuUtilization: gpu.Utilization,
            GpuTemperature: gpu.Temperature,
            GpuPowerWatts: gpu.PowerWatts,
            GpuMemoryUsedMb: gpu.MemoryUsedMb,
            GpuMemoryTotalMb: gpu.MemoryTotalMb,
            GpuPowerState: gpu.PowerState,
            CpuFanRpm: Fan(firmware?.CpuFanRpm),
            GpuFanRpm: Fan(firmware?.GpuFanRpm),
            IsOnBattery: isOnBattery,
            CapturedAtUtc: now)
        {
            CpuPowerWatts = Counter(counters.PackageMilliwatts is > 0 and < 500000 ? counters.PackageMilliwatts / 1000 : null, "W", "PDH Energy Meter")
        };
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
                ["DiskSource"] = "DriveInfo + PhysicalDisk PDH",
                ["NvidiaSmiPath"] = smiPath ?? "NotFound",
                ["CpuTempAndFanRpm"] = "LENOVO_OTHER_METHOD (administrator only)"
            };

            return new FeatureCapability(
                FeatureId.HardwareMonitoring,
                ReadAvailable: true,
                WriteAvailable: false,
                Status: CapabilityAvailability.Available,
                Reason: "Windows system metrics available. NVIDIA readings use the installed NVML driver or nvidia-smi. Firmware sensors require administrator access.",
                Provider: "Win32 + PDH + NVML / nvidia-smi",
                LastCheckedUtc: now,
                Details: details);
        }, ct);
    }

    private static IReadOnlyList<DiskCapacityInfo> ReadDiskCapacities()
    {
        var list = new List<DiskCapacityInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                    continue;
                var total = drive.TotalSize;
                var free = drive.AvailableFreeSpace;
                list.Add(new DiskCapacityInfo(drive.Name.TrimEnd('\\'), drive.VolumeLabel, total, Math.Max(0, total - free), free));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Other volumes and sensors remain readable if a drive is detached or locked.
            }
        }

        return list;
    }

    private async Task<(
        string? Name,
        SensorReading<double> Utilization,
        SensorReading<double> Temperature,
        SensorReading<double> PowerWatts,
        SensorReading<double> MemoryUsedMb,
        SensorReading<double> MemoryTotalMb,
        string? PowerState)> ReadNvidiaTelemetryAsync(
        bool isOnBattery,
        bool allowWakeDgpu,
        bool pauseDgpu,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (pauseDgpu || (isOnBattery && !allowWakeDgpu))
        {
            lock (_nativeLock)
                _nvidia.Dispose();
            var paused = new SensorReading<double>(null, string.Empty,
                pauseDgpu ? SensorStatus.PausedInIntegratedMode : SensorStatus.PausedOnBattery, "nvidia-smi", now);
            return (null, paused with { Unit = "%" }, paused with { Unit = "C" }, paused with { Unit = "W" }, paused with { Unit = "MB" }, paused with { Unit = "MB" }, null);
        }

        if (now >= _nextNvmlAttempt)
        {
            NvidiaSample? sample;
            lock (_nativeLock)
                sample = _disposed ? null : _nvidia.TryRead(now);
            if (sample is { } direct)
                return (direct.Name, direct.Utilization, direct.Temperature, direct.PowerWatts, direct.MemoryUsedMb, direct.MemoryTotalMb, direct.PowerState);
            _nextNvmlAttempt = now.AddSeconds(30);
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
            try
            {
                await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Disposing a Process only releases its handle; stop the timed-out child.
                try
                {
                    if (!proc.HasExited)
                        proc.Kill();
                }
                catch (InvalidOperationException)
                {
                    // The process exited before it could be stopped.
                }

                throw;
            }
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
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

        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var val) && double.IsFinite(val))
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

    public void ReleaseIdleResources()
    {
        lock (_nativeLock)
        {
            _nvidia.Dispose();
            _counters.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_nativeLock)
        {
            _disposed = true;
            ReleaseIdleResources();
        }
    }
}
