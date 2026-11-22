using System.Management;
using System.Security.Principal;
using Utility.Core.Models;
using Utility.Core.Services;
using Utility.Platform.Native;

namespace Utility.Platform.Lenovo;

/// <summary>
/// Direct WMI client for Lenovo ACPI/WMI interfaces under root\wmi.
/// Standard-user sessions map AccessDenied cleanly to InsufficientPrivilege without throwing.
/// </summary>
public sealed partial class LenovoWmiProvider
{
    public static bool IsProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public MachineIdentity ReadMachineIdentity()
    {
        using var board = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        if (board?.GetValue("SystemManufacturer") is string maker && board.GetValue("SystemProductName") is string product &&
            board.GetValue("BIOSVersion") is string firmware && !string.IsNullOrWhiteSpace(product) && !string.IsNullOrWhiteSpace(firmware))
            return new MachineIdentity(maker.Trim(), product.Trim(), board.GetValue("SystemFamily") as string ?? "Unknown",
                board.GetValue("SystemSKU") as string ?? "Unknown", firmware.Trim(), Environment.OSVersion.Version.Build.ToString());

        var manufacturer = "Unknown";
        var model = "Unknown";
        var family = "Unknown";
        var sku = "Unknown";
        var bios = "Unknown";
        var osBuild = Environment.OSVersion.Version.Build.ToString();

        try
        {
            using var csSearcher = new ManagementObjectSearcher(LenovoWmiConstants.CimNamespace, "SELECT Manufacturer, Model, SystemFamily, SystemSKUNumber FROM Win32_ComputerSystem");
            foreach (ManagementObject obj in csSearcher.Get())
            {
                manufacturer = obj["Manufacturer"]?.ToString()?.Trim() ?? manufacturer;
                model = obj["Model"]?.ToString()?.Trim() ?? model;
                family = obj["SystemFamily"]?.ToString()?.Trim() ?? family;
                sku = obj["SystemSKUNumber"]?.ToString()?.Trim() ?? sku;
                break;
            }
        }
        catch
        {
            // Keep fallback values.
        }

        try
        {
            using var biosSearcher = new ManagementObjectSearcher(LenovoWmiConstants.CimNamespace, "SELECT SMBIOSBIOSVersion FROM Win32_BIOS");
            foreach (ManagementObject obj in biosSearcher.Get())
            {
                bios = obj["SMBIOSBIOSVersion"]?.ToString()?.Trim() ?? bios;
                break;
            }
        }
        catch
        {
            // Keep fallback values.
        }

        try
        {
            using var osSearcher = new ManagementObjectSearcher(LenovoWmiConstants.CimNamespace, "SELECT BuildNumber FROM Win32_OperatingSystem");
            foreach (ManagementObject obj in osSearcher.Get())
            {
                osBuild = obj["BuildNumber"]?.ToString()?.Trim() ?? osBuild;
                break;
            }
        }
        catch
        {
            // Keep fallback values.
        }

        return new MachineIdentity(manufacturer, model, family, sku, bios, osBuild);
    }

    public FeatureCapability ProbeWmiClassAccess(FeatureId featureId, string className, string providerLabel)
    {
        var now = DateTimeOffset.UtcNow;
        try
        {
            using var searcher = new ManagementObjectSearcher(LenovoWmiConstants.WmiNamespace, $"SELECT * FROM {className}");
            using var results = searcher.Get();
            var count = 0;
            foreach (ManagementObject _ in results)
            {
                count++;
                if (count >= 1) break;
            }

            return count > 0
                ? new FeatureCapability(featureId, true, false, CapabilityAvailability.Available,
                    $"WMI class {className} readable ({count}+ instance(s)).", providerLabel, now)
                : new FeatureCapability(featureId, false, false, CapabilityAvailability.UnavailableInterface,
                    $"WMI class {className} returned zero instances.", providerLabel, now);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new FeatureCapability(featureId, false, false, CapabilityAvailability.InsufficientPrivilege,
                $"WMI class {className} requires administrator elevation ({ex.Message}).", providerLabel, now);
        }
        catch (ManagementException ex)
        {
            var status = ex.ErrorCode switch
            {
                ManagementStatus.AccessDenied => CapabilityAvailability.InsufficientPrivilege,
                ManagementStatus.NotSupported or ManagementStatus.InvalidClass or ManagementStatus.NotFound => CapabilityAvailability.UnavailableInterface,
                _ => CapabilityAvailability.TransientFailure
            };

            return new FeatureCapability(featureId, false, false, status,
                $"WMI {className} failed ({ex.ErrorCode}): {ex.Message}", providerLabel, now);
        }
        catch (Exception ex)
        {
            return new FeatureCapability(featureId, false, false, CapabilityAvailability.TransientFailure,
                $"WMI {className} probe error: {ex.Message}", providerLabel, now);
        }
    }

    public LiveThermalSnapshot ReadLiveThermalSnapshotDirect()
    {
        var now = DateTimeOffset.UtcNow;
        var (winPowerMode, schemeName) = PowerSchemeInterop.ReadWindowsPowerState();
        var isAc = Win32BatteryInterop.IsAcOnlineQuick();

        try
        {
            var thermalRaw = InvokeGameZoneGetter("GetSmartFanMode");
            var thermalMode = thermalRaw switch
            {
                1u => ThermalMode.Quiet,
                2u => ThermalMode.Balanced,
                3u => ThermalMode.Performance,
                255u => ThermalMode.Custom,
                _ => ThermalMode.Unknown
            };

            var livePower = thermalMode == ThermalMode.Custom && IsKnown83JJFirmware()
                ? Read83JJPowerFields()
                : null;
            return new LiveThermalSnapshot(
                ThermalMode: thermalMode,
                WindowsPowerMode: winPowerMode,
                ActivePowerSchemeName: schemeName,
                IsOnAcPower: isAc,
                LivePowerValues: livePower,
                MatchedProfileId: livePower is null ? null
                    : ProfileSeedFactory.CreateEditableSeeds().FirstOrDefault(profile =>
                        profile.Power == livePower && profile.WindowsPowerMode == winPowerMode)?.Id,
                IsVerifiedLiveRead: thermalMode != ThermalMode.Unknown,
                StatusNote: livePower is null
                    ? $"Lenovo thermal mode code {thermalRaw}; no verified power readings."
                    : "Custom-mode power limits read from firmware.",
                CapturedAtUtc: now);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException || (ex is ManagementException mex && mex.ErrorCode == ManagementStatus.AccessDenied))
        {
            return new LiveThermalSnapshot(
                ThermalMode.Unknown,
                winPowerMode,
                schemeName,
                isAc,
                null,
                null,
                false,
                "Lenovo thermal/power WMI access denied in standard-user session; click Refresh (Elevated) to read live firmware state.",
                now);
        }
        catch (Exception ex)
        {
            return new LiveThermalSnapshot(
                ThermalMode.Unknown,
                winPowerMode,
                schemeName,
                isAc,
                null,
                null,
                false,
                $"WMI live thermal read failed: {ex.Message}",
                now);
        }
    }

    internal bool IsKnown83JJFirmware()
    {
        var machine = ReadMachineIdentity();
        return machine.IsTargetLegion83JJ && machine.BiosVersion == "PTCN18WW";
    }

    private ProfilePowerSettings Read83JJPowerFields()
    {
        using var instance = GetSingleWmiInstance(LenovoWmiConstants.OtherMethodClass);
        int? Read(string key) => Read83JJPowerField(instance, key);

        return new ProfilePowerSettings
        {
            CpuLongTermPowerLimitW = Read(nameof(ProfilePowerSettings.CpuLongTermPowerLimitW)),
            CpuCrossLoadingPowerLimitW = Read(nameof(ProfilePowerSettings.CpuCrossLoadingPowerLimitW)),
            CpuLongTermPowerLimitTau = Read(nameof(ProfilePowerSettings.CpuLongTermPowerLimitTau)),
            CpuShortTermPowerLimitW = Read(nameof(ProfilePowerSettings.CpuShortTermPowerLimitW)),
            CpuThermalControlCircuitC = Read(nameof(ProfilePowerSettings.CpuThermalControlCircuitC)),
            GpuDynamicBoostW = Read(nameof(ProfilePowerSettings.GpuDynamicBoostW)),
            GpuToCpuDynamicBoostW = Read(nameof(ProfilePowerSettings.GpuToCpuDynamicBoostW)),
            GpuConfigurableTgpW = Read(nameof(ProfilePowerSettings.GpuConfigurableTgpW)),
            GpuTargetTemperatureLimitC = Read(nameof(ProfilePowerSettings.GpuTargetTemperatureLimitC)),
            TotalProcessorPowerTargetAcW = Read(nameof(ProfilePowerSettings.TotalProcessorPowerTargetAcW))
        };
    }

    private static int? Read83JJPowerField(ManagementObject instance, string key)
    {
        try
        {
            const string method = "GetFeatureValue";
            using var input = instance.GetMethodParameters(method);
            // Range descriptors encode the power mode in byte 1; live calls use mode-independent IDs.
            input["IDs"] = LenovoWmiConstants.PowerFieldCapabilityIds[key] & 0xFFFF00FFu;
            using var output = instance.InvokeMethod(method, input, null);
            return output?["value"] is uint value && value <= int.MaxValue ? (int)value : null;
        }
        catch (ManagementException)
        {
            return null;
        }
    }

    public OperationResult<LiveThermalSnapshot> ApplyOwnerPowerPresetDirect(string? profileId)
    {
        if (profileId is not (ProfileSeedFactory.DailyId or ProfileSeedFactory.GamingId) || !IsKnown83JJFirmware())
            return OperationResult<LiveThermalSnapshot>.Fail("UNSUPPORTED_PRESET", "This preset requires Lenovo 83JJ and BIOS PTCN18WW.");

        return Run83JJWrite(() => Apply83JJPowerSwitch(ProfileSeedFactory.CreatePristineReference(profileId)));
    }

    public OperationResult<CoolingCycleResult> RunCoolingCycleDirect(CancellationToken stop)
    {
        if (!IsKnown83JJFirmware())
            return OperationResult<CoolingCycleResult>.Fail("COOLING_UNAVAILABLE", "Cooling requires Lenovo 83JJ and BIOS PTCN18WW.");

        return Run83JJWrite(() =>
        {
            try
            {
                return Run83JJCoolingCycle(stop);
            }
            catch (ManagementException ex)
            {
                return OperationResult<CoolingCycleResult>.Fail("COOLING_UNAVAILABLE", ex.Message);
            }
        });
    }

    private OperationResult<CoolingCycleResult> Run83JJCoolingCycle(CancellationToken stop)
    {
        if (!Win32BatteryInterop.IsAcOnlineQuick())
            return OperationResult<CoolingCycleResult>.Fail("COOLING_REQUIRES_AC", "Connect AC power before starting the cooling cycle.");

        using var instance = GetSingleWmiInstance(LenovoWmiConstants.OtherMethodClass);
        var initial = Read83JJFeature(instance, LenovoWmiConstants.CapFanFullSpeed);
        if (initial != 0)
            return OperationResult<CoolingCycleResult>.Fail(initial == 1 ? "COOLING_ALREADY_ACTIVE" : "COOLING_UNAVAILABLE",
                "The initial fan setting is unavailable or already at full speed.");

        var start = ReadCoolingTemperatures(instance);
        if (start is { CpuC: null, GpuC: null })
            return OperationResult<CoolingCycleResult>.Fail("COOLING_SENSOR_UNAVAILABLE", "No valid firmware temperature reading.");
        if (stop.IsCancellationRequested)
            return OperationResult<CoolingCycleResult>.Cancelled("Cooling cycle cancelled.");

        var end = start;
        var reason = CoolingStopReason.TimeLimit;
        string? cycleError = null;
        try
        {
            Set83JJFullSpeed(instance, 1);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var duration = TimeSpan.FromSeconds(30);
            var sampleInterval = TimeSpan.FromSeconds(1);
            var consecutiveCoolSamples = 0;
            while (true)
            {
                var remaining = duration - timer.Elapsed;
                if (remaining <= TimeSpan.Zero)
                    break;
                if (stop.WaitHandle.WaitOne(remaining < sampleInterval ? remaining : sampleInterval))
                {
                    reason = CoolingStopReason.Stopped;
                    break;
                }
                if (timer.Elapsed >= duration)
                    break;
                if (!Win32BatteryInterop.IsAcOnlineQuick())
                {
                    reason = CoolingStopReason.AcDisconnected;
                    break;
                }
                end = ReadCoolingTemperatures(instance);
                if ((start.CpuC.HasValue && end.CpuC is null) || (start.GpuC.HasValue && end.GpuC is null) ||
                    end is { CpuC: null, GpuC: null })
                {
                    reason = CoolingStopReason.SensorUnavailable;
                    break;
                }
                var cool = (end.CpuC is null or <= 65) && (end.GpuC is null or <= 60);
                consecutiveCoolSamples = cool ? consecutiveCoolSamples + 1 : 0;
                if (timer.Elapsed >= TimeSpan.FromSeconds(8) && consecutiveCoolSamples >= 2)
                {
                    reason = CoolingStopReason.TemperatureReached;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            cycleError = ex.Message;
        }

        // Restore even when the enable call reports an error after changing firmware.
        try
        {
            Set83JJFullSpeed(instance, 0);
        }
        catch
        {
            try
            {
                using var retry = GetSingleWmiInstance(LenovoWmiConstants.OtherMethodClass);
                Set83JJFullSpeed(retry, 0);
            }
            catch (Exception ex)
            {
                return OperationResult<CoolingCycleResult>.Fail("RESTORE_UNVERIFIED", $"Could not verify fan restoration: {ex.Message}");
            }
        }

        return cycleError is null
            ? OperationResult<CoolingCycleResult>.Ok(new(start, end, reason), "The previous fan setting was restored.")
            : OperationResult<CoolingCycleResult>.Fail("ROLLED_BACK", $"Cooling failed and the previous fan setting was restored. {cycleError}");
    }

    private static CoolingTemperatures ReadCoolingTemperatures(ManagementObject instance)
    {
        int? ReadTemperature(uint id)
        {
            try
            {
                var value = Read83JJFeature(instance, id);
                return value is >= 10 and <= 115 ? (int)value.Value : null;
            }
            catch (ManagementException)
            {
                return null;
            }
        }
        return new CoolingTemperatures(ReadTemperature(LenovoWmiConstants.CapCpuCurrentTemp),
            ReadTemperature(LenovoWmiConstants.CapGpuCurrentTemp));
    }

    /// <summary>
    /// Reads live CPU temperature and fan speeds from LENOVO_OTHER_METHOD. Windows grants this
    /// class to administrators only, so a standard-user process gets null (callers show "admin required").
    /// Out-of-range firmware values are treated as missing rather than shown.
    /// </summary>
    public static FirmwareSensorSample? TryReadFirmwareSensors()
    {
        if (!IsProcessElevated())
            return null;
        using var reader = new FirmwareSensorReader();
        return reader.Read();
    }

    /// <summary>Owns the read-only WMI instance for one sequential sensor session.</summary>
    public sealed class FirmwareSensorReader : IDisposable
    {
        private ManagementObject? _instance;
        private ManagementBaseObject? _input;

        public FirmwareSensorSample? Read()
        {
            try
            {
                _instance ??= GetSingleWmiInstance(LenovoWmiConstants.OtherMethodClass);
                _input ??= _instance.GetMethodParameters("GetFeatureValue");
                int? Value(uint id, uint min, uint max)
                {
                    _input["IDs"] = id;
                    try
                    {
                        using var output = _instance.InvokeMethod("GetFeatureValue", _input, null);
                        return output?["value"] is uint v && v >= min && v <= max ? (int)v : null;
                    }
                    catch (ManagementException)
                    {
                        return null;
                    }
                }
                var sample = new FirmwareSensorSample(Value(LenovoWmiConstants.CapCpuCurrentTemp, 10, 115),
                    Value(LenovoWmiConstants.CapCpuFanSpeed, 0, 10000), Value(LenovoWmiConstants.CapGpuFanSpeed, 0, 10000));
                if (sample is { CpuTemperatureC: null, CpuFanRpm: null, GpuFanRpm: null })
                    Dispose(); // Reconnect on the next poll, including after sleep or provider restart.
                return sample;
            }
            catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            {
                Dispose();
                return null;
            }
        }

        public void Dispose()
        {
            _input?.Dispose();
            _input = null;
            _instance?.Dispose();
            _instance = null;
        }
    }

    private static uint? Read83JJFeature(ManagementObject instance, uint id)
    {
        const string method = "GetFeatureValue";
        using var input = instance.GetMethodParameters(method);
        input["IDs"] = id;
        using var output = instance.InvokeMethod(method, input, null);
        return output?["value"] is uint value ? value : null;
    }

    private static void Set83JJFullSpeed(ManagementObject instance, uint value)
    {
        const string method = "SetFeatureValue";
        using var input = instance.GetMethodParameters(method);
        input["IDs"] = LenovoWmiConstants.CapFanFullSpeed;
        input["value"] = value;
        using var output = instance.InvokeMethod(method, input, null);
        VerifyFirmwareValue(() => Read83JJFeature(instance, LenovoWmiConstants.CapFanFullSpeed) ?? uint.MaxValue,
            value, "fan full-speed state");
    }

    internal static OperationResult<T> Run83JJWrite<T>(Func<OperationResult<T>> action)
    {
        using var writeLock = new Mutex(false, @"Local\Utility83JJPowerSwitch");
        try
        {
            if (!writeLock.WaitOne(0))
                return OperationResult<T>.Fail("BUSY", "Another Utility operation is controlling the firmware.");
        }
        catch (AbandonedMutexException)
        {
            // A fresh preflight rejects settings left in an unknown or partial state.
        }

        try
        {
            return action();
        }
        finally
        {
            writeLock.ReleaseMutex();
        }
    }

    private OperationResult<LiveThermalSnapshot> Apply83JJPowerSwitch(PerformanceProfile profile)
    {
        var baseline = ReadLiveThermalSnapshotDirect();
        if (!baseline.IsVerifiedLiveRead || !baseline.IsOnAcPower || baseline.WindowsPowerMode == WindowsPowerMode.Unknown)
        {
            return OperationResult<LiveThermalSnapshot>.Fail("PREFLIGHT_REJECTED",
                "Connect AC power and verify that the Lenovo firmware interface is available.");
        }

        var enteringCustom = baseline.ThermalMode != ThermalMode.Custom;
        var overlayAttempted = false;
        ProfilePowerSettings? original = null;
        LenovoFanCapture? fanBefore = null;
        var attempted = new HashSet<string>(StringComparer.Ordinal);
        var failureCode = "PREFLIGHT_REJECTED";
        try
        {
            if (enteringCustom)
                SetAndVerify83JJThermalMode(ThermalMode.Custom);

            original = enteringCustom ? Read83JJPowerFields() : baseline.LivePowerValues;
            if (original is null || !IsValid83JJBaseline(original))
                throw new InvalidOperationException("The firmware did not return ten power values within its supported capabilities.");

            failureCode = "FAN_BASELINE_UNAVAILABLE";
            fanBefore = ReadFanCurveDirect().LiveCapture
                ?? throw new InvalidOperationException("The existing custom fan table could not be read.");
            failureCode = "PREFLIGHT_REJECTED";
            Write83JJPowerFields(original, profile.Power, requireAc: true, attempted.Add);
            if (PowerSchemeInterop.ReadWindowsPowerState().Mode != profile.WindowsPowerMode)
            {
                overlayAttempted = true;
                SetAndVerifyWindowsPowerMode(profile.WindowsPowerMode);
            }
            var live = Verify83JJPowerSwitch(profile.Power, fanBefore);
            if (live.WindowsPowerMode != profile.WindowsPowerMode)
                throw new InvalidOperationException("Windows power mode changed during verification.");
            return OperationResult<LiveThermalSnapshot>.Ok(live,
                "All ten power fields and the Windows power mode were verified. The existing fan table was preserved.");
        }
        catch (Exception applyError)
        {
            try
            {
                try
                {
                    if (attempted.Count > 0)
                    {
                        Write83JJPowerFields(Read83JJPowerFields(), original!, requireAc: false, include: attempted.Contains);
                        _ = Verify83JJPowerSwitch(original!, fanBefore!);
                    }
                }
                finally
                {
                    try
                    {
                        if (enteringCustom)
                            SetAndVerify83JJThermalMode(baseline.ThermalMode);
                    }
                    finally
                    {
                        if (enteringCustom || overlayAttempted)
                            SetAndVerifyWindowsPowerMode(baseline.WindowsPowerMode);
                    }
                }
                if (!enteringCustom && !overlayAttempted && attempted.Count == 0)
                    return OperationResult<LiveThermalSnapshot>.Fail(failureCode, applyError.Message);
                return OperationResult<LiveThermalSnapshot>.Fail("ROLLED_BACK",
                    $"The switch failed and the previous firmware state was restored. {applyError.Message}");
            }
            catch (Exception restoreError)
            {
                return OperationResult<LiveThermalSnapshot>.Fail("RESTORE_UNVERIFIED",
                    $"Firmware recovery could not be verified: {restoreError.Message}");
            }
        }
    }

    private static void SetAndVerifyWindowsPowerMode(WindowsPowerMode mode)
    {
        if (PowerSchemeInterop.ReadWindowsPowerState().Mode != mode &&
            !PowerSchemeInterop.TrySetWindowsPowerMode(mode, out var error))
            throw new InvalidOperationException(error);

        VerifyFirmwareValue(() => (uint)PowerSchemeInterop.ReadWindowsPowerState().Mode, (uint)mode, "Windows power mode");
    }

    private bool IsValid83JJBaseline(ProfilePowerSettings power)
    {
        if (ProfileSeedFactory.CreateEditableSeeds().Any(profile => profile.Power == power))
            return true;

        var bounds = ReadLiveFieldBoundsDirect();
        return power.AsFieldMap().All(field => field.Value is int value &&
            bounds.TryGetValue(field.Key, out var limit) && value >= limit.Min && value <= limit.Max);
    }

    private void SetAndVerify83JJThermalMode(ThermalMode mode)
    {
        if (InvokeGameZoneGetter("GetSmartFanMode") == (uint)mode)
            return;

        const string method = "SetSmartFanMode";
        using var instance = GetSingleWmiInstance(LenovoWmiConstants.GameZoneDataClass);
        using var input = instance.GetMethodParameters(method);
        input["Data"] = (uint)mode;
        using var output = instance.InvokeMethod(method, input, null);
        VerifyFirmwareValue(() => InvokeGameZoneGetter("GetSmartFanMode"), (uint)mode, "thermal mode");
    }

    private static void VerifyFirmwareValue(Func<uint> read, uint expected, string setting)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            if (read() == expected)
                return;
            Thread.Sleep(200);
        }
        throw new InvalidOperationException($"Firmware did not verify {setting}.");
    }

    private void Write83JJPowerFields(
        ProfilePowerSettings current, ProfilePowerSettings target, bool requireAc,
        Func<string, bool>? markAttempted = null, Predicate<string>? include = null)
    {
        var active = current.AsFieldMap();
        var requested = target.AsFieldMap();
        if (active.Values.Any(value => value is null))
            throw new InvalidOperationException("Incomplete power read-back.");

        using var instance = GetSingleWmiInstance(LenovoWmiConstants.OtherMethodClass);
        foreach (var key in LenovoWmiConstants.OrderedPowerFieldKeys
                     .Where(key => (include?.Invoke(key) ?? true) && requested[key] != active[key])
                     .OrderBy(key => requested[key] < active[key] ? 0 : 1))
        {
            if (InvokeGameZoneGetter("GetSmartFanMode") != (uint)ThermalMode.Custom ||
                (requireAc && !Win32BatteryInterop.IsAcOnlineQuick()))
                throw new InvalidOperationException("The machine left custom mode or AC power during the switch.");

            const string method = "SetFeatureValue";
            using var input = instance.GetMethodParameters(method);
            input["IDs"] = LenovoWmiConstants.PowerFieldCapabilityIds[key] & 0xFFFF00FFu;
            input["value"] = checked((uint)requested[key]!.Value);
            markAttempted?.Invoke(key);
            using var output = instance.InvokeMethod(method, input, null);
            if (Read83JJPowerField(instance, key) != requested[key])
            {
                Thread.Sleep(200);
                if (Read83JJPowerField(instance, key) != requested[key])
                    throw new InvalidOperationException($"Read-back mismatch for {key}.");
            }
        }
    }

    private LiveThermalSnapshot Verify83JJPowerSwitch(ProfilePowerSettings expected, LenovoFanCapture fanBaseline)
    {
        var live = ReadLiveThermalSnapshotDirect();
        var fan = ReadFanCurveDirect().LiveCapture;
        if (live.LivePowerValues != expected || fan is null || !fan.Stages.SequenceEqual(fanBaseline.Stages))
            throw new InvalidOperationException("The power fields or the preserved fan table did not match.");
        return live;
    }

    public IReadOnlyDictionary<string, (int Min, int Max, int Step, int Default)> ReadLiveFieldBoundsDirect()
    {
        var map = new Dictionary<uint, (int Min, int Max, int Step, int Default)>();
        using var searcher = new ManagementObjectSearcher(LenovoWmiConstants.WmiNamespace, $"SELECT IDs, MinValue, MaxValue, Step, DefaultValue FROM {LenovoWmiConstants.CapabilityData01Class}");
        foreach (ManagementObject obj in searcher.Get())
        {
            var id = Convert.ToUInt32(obj["IDs"]);
            var min = Convert.ToInt32(obj["MinValue"]);
            var max = Convert.ToInt32(obj["MaxValue"]);
            var step = Math.Max(1, Convert.ToInt32(obj["Step"]));
            var def = Convert.ToInt32(obj["DefaultValue"]);
            map[id] = (min, max, step, def);
        }

        var result = new Dictionary<string, (int Min, int Max, int Step, int Default)>(StringComparer.Ordinal);
        foreach (var kv in LenovoWmiConstants.PowerFieldCapabilityIds)
        {
            if (map.TryGetValue(kv.Value, out var bounds))
            {
                result[kv.Key] = bounds;
            }
        }

        return result;
    }

    public FanCurveProfile ReadFanCurveDirect()
    {
        var channels = new List<FanChannelCurve>();
        var customTables = new Dictionary<(int Fan, int Sensor), IReadOnlyList<FanCurvePoint>>();
        using var searcher = new ManagementObjectSearcher(LenovoWmiConstants.WmiNamespace, $"SELECT * FROM {LenovoWmiConstants.FanTableDataClass}");
        foreach (ManagementObject obj in searcher.Get())
        {
            var mode = Convert.ToInt32(obj["Mode"]);
            var fanId = Convert.ToInt32(obj["Fan_Id"]);
            var sensorId = Convert.ToInt32(obj["Sensor_ID"]);
            var minTemp = Convert.ToInt32(obj["MinSensorTemperature"]);
            var maxTemp = Convert.ToInt32(obj["MaxSensorTemperature"]);
            var tempStep = Math.Max(1, Convert.ToInt32(obj["SensorTemperatureStep"]));
            var minSpeed = Convert.ToInt32(obj["CurrentFanMinSpeed"]);
            var maxSpeed = Convert.ToInt32(obj["CurrentFanMaxSpeed"]);
            var speedStep = Math.Max(1, Convert.ToInt32(obj["FanSpeedStep"]));

            var fanData = obj["FanTable_Data"] as ushort[] ?? [];
            var sensorData = obj["SensorTable_Data"] as ushort[] ?? [];
            var fanLength = Convert.ToInt32(obj["FanTable_Len"]);
            var sensorLength = Convert.ToInt32(obj["SensorTable_Len"]);
            if (fanLength <= 0 || fanLength != sensorLength ||
                fanLength > fanData.Length || sensorLength > sensorData.Length)
            {
                continue;
            }

            var count = fanLength;
            var points = new List<FanCurvePoint>(count);
            for (var i = 0; i < count; i++)
            {
                points.Add(new FanCurvePoint(sensorData[i], fanData[i], "Firmware value"));
            }

            if (mode == (int)ThermalMode.Custom)
                customTables.TryAdd((fanId, sensorId), points);

            channels.Add(new FanChannelCurve(
                ChannelId: fanId,
                ChannelName: $"Mode 0x{mode:X2}, fan {fanId}",
                SensorName: $"Sensor {sensorId}",
                Points: points,
                MinTempC: minTemp,
                MaxTempC: maxTemp,
                TempStep: tempStep,
                MinSpeed: minSpeed,
                MaxSpeed: maxSpeed,
                SpeedStep: speedStep,
                SpeedUnit: "Firmware value"));
        }

        LenovoFanCapture? capture = null;
        string? diagnostic = null;
        if (channels.Count != 0)
        {
            try
            {
                capture = TryReadActive83JJFanStages(customTables);
            }
            catch (ManagementException ex)
            {
                diagnostic = $" Active stage query failed ({ex.ErrorCode}).";
            }
        }

        return new FanCurveProfile(
            Status: channels.Count > 0 ? FanCurveStatus.Captured : FanCurveStatus.PendingCapture,
            SourceDescription: capture is not null
                ? "Active custom-mode stages read from firmware."
                : channels.Count > 0
                    ? "Raw firmware tables read; active custom stages are unavailable." + diagnostic
                    : "No firmware tables with matching sensor and speed points were returned.",
            Channels: channels)
        {
            LiveCapture = capture
        };
    }

    private LenovoFanCapture? TryReadActive83JJFanStages(
        IReadOnlyDictionary<(int Fan, int Sensor), IReadOnlyList<FanCurvePoint>> tables)
    {
        if (!IsKnown83JJFirmware() ||
            InvokeGameZoneGetter("GetSmartFanMode") != (uint)ThermalMode.Custom ||
            !tables.TryGetValue((1, 4), out var cpu) || !tables.TryGetValue((2, 5), out var gpu) ||
            !tables.TryGetValue((1, 1), out var auxiliary))
        {
            return null;
        }

        using var fanMethod = GetSingleWmiInstance(LenovoWmiConstants.FanMethodClass);
        var levels = ReadFanLevels(fanMethod, fan: 1, sensor: 4);
        if (levels is null ||
            ReadFanLevels(fanMethod, fan: 2, sensor: 5)?.SequenceEqual(levels) != true ||
            ReadFanLevels(fanMethod, fan: 1, sensor: 1)?.SequenceEqual(levels) != true)
        {
            return null;
        }

        return LenovoFanCapture.From83JJTables(levels, cpu, gpu, auxiliary);
    }

    private static int[]? ReadFanLevels(ManagementObject fanMethod, byte fan, byte sensor)
    {
        const string method = "Fan_Get_Table";
        using var input = fanMethod.GetMethodParameters(method);
        input["FanID"] = fan;
        input["SensorID"] = sensor;
        using var output = fanMethod.InvokeMethod(method, input, null);
        // System.Management surfaces the firmware's output parameters and omits the provider return flag.
        if (output?["FanTable"] is not uint[] levels ||
            output["FanTableSize"] is not uint count || count != 10 || levels.Length < count ||
            levels.Take((int)count).Any(level => level is < 1 or > 10))
        {
            return null;
        }

        return levels.Take((int)count).Select(level => (int)level).ToArray();
    }

    private uint InvokeGameZoneGetter(string methodName)
    {
        using var instance = GetSingleWmiInstance(LenovoWmiConstants.GameZoneDataClass);
        using var outParams = instance.InvokeMethod(methodName, null, null);
        return outParams?["Data"] is uint result
            ? result
            : throw new ManagementException($"WMI method {methodName} did not return a value.");
    }

    private static ManagementObject GetSingleWmiInstance(string className)
    {
        using var searcher = new ManagementObjectSearcher(LenovoWmiConstants.WmiNamespace, $"SELECT * FROM {className}");
        using var results = searcher.Get();
        foreach (ManagementObject obj in results)
        {
            return obj;
        }

        throw new ManagementException($"No WMI instances found for {className}.");
    }
}
