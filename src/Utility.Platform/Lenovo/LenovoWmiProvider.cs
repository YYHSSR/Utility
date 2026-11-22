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
public sealed class LenovoWmiProvider
{
    public static bool IsProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public MachineIdentity ReadMachineIdentity()
    {
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
                ? new FeatureCapability(featureId, true, IsProcessElevated(), CapabilityAvailability.Available,
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

            var power = ReadLivePowerValuesDirect();
            return new LiveThermalSnapshot(
                ThermalMode: thermalMode,
                WindowsPowerMode: winPowerMode,
                ActivePowerSchemeName: schemeName,
                IsOnAcPower: isAc,
                LivePowerValues: power,
                MatchedProfileId: null,
                IsVerifiedLiveRead: true,
                StatusNote: $"Verified via {LenovoWmiConstants.GameZoneDataClass} + {LenovoWmiConstants.OtherMethodClass}.",
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

    public ProfilePowerSettings ReadLivePowerValuesDirect()
    {
        int? ReadField(string fieldKey)
        {
            var id = LenovoWmiConstants.PowerFieldCapabilityIds[fieldKey];
            return TryGetFeatureValue(id, out var val) ? (int)val : null;
        }

        return new ProfilePowerSettings
        {
            CpuLongTermPowerLimitW = ReadField(nameof(ProfilePowerSettings.CpuLongTermPowerLimitW)),
            CpuCrossLoadingPowerLimitW = ReadField(nameof(ProfilePowerSettings.CpuCrossLoadingPowerLimitW)),
            CpuLongTermPowerLimitTau = ReadField(nameof(ProfilePowerSettings.CpuLongTermPowerLimitTau)),
            CpuShortTermPowerLimitW = ReadField(nameof(ProfilePowerSettings.CpuShortTermPowerLimitW)),
            CpuThermalControlCircuitC = ReadField(nameof(ProfilePowerSettings.CpuThermalControlCircuitC)),
            GpuDynamicBoostW = ReadField(nameof(ProfilePowerSettings.GpuDynamicBoostW)),
            GpuToCpuDynamicBoostW = ReadField(nameof(ProfilePowerSettings.GpuToCpuDynamicBoostW)),
            GpuConfigurableTgpW = ReadField(nameof(ProfilePowerSettings.GpuConfigurableTgpW)),
            GpuTargetTemperatureLimitC = ReadField(nameof(ProfilePowerSettings.GpuTargetTemperatureLimitC)),
            TotalProcessorPowerTargetAcW = ReadField(nameof(ProfilePowerSettings.TotalProcessorPowerTargetAcW))
        };
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

    public bool SetThermalModeDirect(ThermalMode mode, out string message)
    {
        var code = mode switch
        {
            ThermalMode.Quiet => 1u,
            ThermalMode.Balanced => 2u,
            ThermalMode.Performance => 3u,
            ThermalMode.Custom => 255u,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported thermal mode.")
        };

        InvokeGameZoneSetter("SetSmartFanMode", "Data", code);
        var verify = InvokeGameZoneGetter("GetSmartFanMode");
        if (verify == code)
        {
            message = $"SetSmartFanMode({code}) verified.";
            return true;
        }

        message = $"SetSmartFanMode({code}) read back {verify}.";
        return false;
    }

    public (IReadOnlyList<string> Applied, IReadOnlyList<string> Failed, string Message) ApplyPowerFieldsDirect(
        IReadOnlyDictionary<string, int> backendValuesByField)
    {
        var applied = new List<string>();
        var failed = new List<string>();

        foreach (var key in LenovoWmiConstants.OrderedPowerFieldKeys)
        {
            if (!backendValuesByField.TryGetValue(key, out var val))
            {
                continue;
            }

            if (!LenovoWmiConstants.PowerFieldCapabilityIds.TryGetValue(key, out var capId))
            {
                failed.Add(key);
                return (applied, failed, $"Unknown capability mapping for {key}.");
            }

            try
            {
                SetFeatureValue(capId, unchecked((uint)val));
                applied.Add(key);
            }
            catch (Exception ex)
            {
                failed.Add(key);
                return (applied, failed, $"Failed setting {key} (0x{capId:X8}) = {val}: {ex.Message}");
            }
        }

        return (applied, failed, $"Applied {applied.Count} power fields.");
    }

    public GraphicsStatusSnapshot ReadGraphicsStatusDirect(GraphicsRoutingMode? pendingMode)
    {
        var now = DateTimeOffset.UtcNow;
        try
        {
            var gsyncSup = InvokeGameZoneGetter("IsSupportGSync");
            var igpuSup = InvokeGameZoneGetter("IsSupportIGPUMode");
            var gsyncState = gsyncSup > 0 ? InvokeGameZoneGetter("GetGSyncStatus") : 0u;
            var igpuState = igpuSup > 0 ? InvokeGameZoneGetter("GetIGPUModeStatus") : 0u;

            var supported = new List<GraphicsModeOption>();
            if (gsyncSup > 0 || igpuSup > 0)
            {
                supported.Add(new GraphicsModeOption(GraphicsRoutingMode.Hybrid, true, true, true, "Hybrid graphics (iGPU + dGPU dynamic routing)."));
            }

            if (gsyncSup > 0)
            {
                supported.Add(new GraphicsModeOption(GraphicsRoutingMode.DgpuOnly, true, true, true, "Discrete GPU only (MUX switched to RTX dGPU; requires reboot)."));
            }

            if (igpuSup > 0)
            {
                supported.Add(new GraphicsModeOption(GraphicsRoutingMode.HybridIgpuOnly, false, true, true, "Hybrid iGPU-only mode."));
                supported.Add(new GraphicsModeOption(GraphicsRoutingMode.HybridAuto, false, true, true, "Hybrid auto mode."));
            }

            // GSyncStatus=0 means MUX is in dGPU-only mode on Legion firmware; 1 means Hybrid enabled.
            var effective = (gsyncSup > 0 && gsyncState == 0)
                ? GraphicsRoutingMode.DgpuOnly
                : igpuState switch
                {
                    1u => GraphicsRoutingMode.HybridIgpuOnly,
                    2u => GraphicsRoutingMode.HybridAuto,
                    _ => GraphicsRoutingMode.Hybrid
                };

            return new GraphicsStatusSnapshot(
                EffectiveMode: effective,
                PendingMode: pendingMode,
                RebootRequired: pendingMode is not null && pendingMode != effective,
                SupportedModes: supported,
                IsVerifiedLiveRead: true,
                StatusNote: $"Verified GSyncSupport={gsyncSup}, GSyncStatus={gsyncState}, IGPUSupport={igpuSup}, IGPUMode={igpuState}.",
                CapturedAtUtc: now);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException || (ex is ManagementException mex && mex.ErrorCode == ManagementStatus.AccessDenied))
        {
            return new GraphicsStatusSnapshot(
                GraphicsRoutingMode.Unknown,
                pendingMode,
                pendingMode is not null,
                [],
                false,
                "Graphics routing WMI requires elevation in standard-user mode.",
                now);
        }
        catch (Exception ex)
        {
            return new GraphicsStatusSnapshot(
                GraphicsRoutingMode.Unknown,
                pendingMode,
                pendingMode is not null,
                [],
                false,
                $"Graphics WMI read failed: {ex.Message}",
                now);
        }
    }

    public FanCurveProfile ReadFanCurveDirect()
    {
        var channels = new List<FanChannelCurve>();
        using var searcher = new ManagementObjectSearcher(LenovoWmiConstants.WmiNamespace, $"SELECT * FROM {LenovoWmiConstants.FanTableDataClass}");
        foreach (ManagementObject obj in searcher.Get())
        {
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
            var count = Math.Min(fanData.Length, sensorData.Length);
            var points = new List<FanCurvePoint>(count);
            for (var i = 0; i < count; i++)
            {
                points.Add(new FanCurvePoint(sensorData[i], fanData[i], "RPM"));
            }

            channels.Add(new FanChannelCurve(
                ChannelId: fanId,
                ChannelName: $"Fan {fanId}",
                SensorName: $"Sensor {sensorId}",
                Points: points,
                MinTempC: minTemp,
                MaxTempC: maxTemp,
                TempStep: tempStep,
                MinSpeed: minSpeed,
                MaxSpeed: maxSpeed,
                SpeedStep: speedStep,
                SpeedUnit: "RPM"));
        }

        return new FanCurveProfile(
            Status: channels.Count > 0 ? FanCurveStatus.Verified : FanCurveStatus.PendingCapture,
            SourceDescription: channels.Count > 0 ? "Read from LENOVO_FAN_TABLE_DATA" : "No firmware fan table instances found",
            Channels: channels);
    }

    private uint InvokeGameZoneGetter(string methodName)
    {
        using var instance = GetSingleWmiInstance(LenovoWmiConstants.GameZoneDataClass);
        using var outParams = instance.InvokeMethod(methodName, null, null);
        return Convert.ToUInt32(outParams?["Data"] ?? 0);
    }

    private void InvokeGameZoneSetter(string methodName, string argName, uint value)
    {
        using var instance = GetSingleWmiInstance(LenovoWmiConstants.GameZoneDataClass);
        using var inParams = instance.GetMethodParameters(methodName);
        inParams[argName] = value;
        instance.InvokeMethod(methodName, inParams, null);
    }

    private bool TryGetFeatureValue(uint capabilityId, out uint value)
    {
        value = 0;
        try
        {
            using var instance = GetSingleWmiInstance(LenovoWmiConstants.OtherMethodClass);
            using var inParams = instance.GetMethodParameters("GetFeatureValue");
            inParams["IDs"] = capabilityId;
            using var outParams = instance.InvokeMethod("GetFeatureValue", inParams, null);
            value = Convert.ToUInt32(outParams?["value"] ?? 0);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void SetFeatureValue(uint capabilityId, uint value)
    {
        using var instance = GetSingleWmiInstance(LenovoWmiConstants.OtherMethodClass);
        using var inParams = instance.GetMethodParameters("SetFeatureValue");
        inParams["IDs"] = capabilityId;
        inParams["value"] = value;
        instance.InvokeMethod("SetFeatureValue", inParams, null);
    }

    private static ManagementObject GetSingleWmiInstance(string className)
    {
        using var searcher = new ManagementObjectSearcher(LenovoWmiConstants.WmiNamespace, $"SELECT * FROM {className}");
        foreach (ManagementObject obj in searcher.Get())
        {
            return obj;
        }

        throw new ManagementException($"No WMI instances found for {className}.");
    }
}
