using System.IO.Pipes;
using System.Management;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Utility.Core.Models;
using Utility.Core.Services;
using Utility.Platform.Lenovo;
using Utility.Platform.Providers;

namespace Utility.ElevatedWorker;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        if (!LenovoWmiProvider.IsProcessElevated())
        {
            return 5; // ERROR_ACCESS_DENIED
        }

        if (args.Length == 2 && args[0] == "--probe-report")
        {
            return await WriteProbeReportAsync(args[1]).ConfigureAwait(false);
        }

        if (args.Length != 4 || args[0] != "--pipe" || args[2] != "--token")
        {
            return 87; // ERROR_INVALID_PARAMETER
        }

        var pipeName = args[1];
        var expectedToken = args[3];
        if (string.IsNullOrWhiteSpace(pipeName) || string.IsNullOrWhiteSpace(expectedToken))
        {
            return 87;
        }

        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(10000).ConfigureAwait(false);

            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            var line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line))
            {
                return 1;
            }

            var request = JsonSerializer.Deserialize<ElevatedRequest>(line, JsonOptions);
            var response = request is null || !string.Equals(request.AuthToken, expectedToken, StringComparison.Ordinal)
                ? new ElevatedResponse { Success = false, ErrorCode = "UNAUTHORIZED_PIPE_CLIENT", Message = "Invalid IPC authentication token." }
                : await HandleRequestAsync(request).ConfigureAwait(false);

            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions)).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
            return response.Success ? 0 : 2;
        }
        catch
        {
            return 1;
        }
    }

    private static async Task<int> WriteProbeReportAsync(string outputPath)
    {
        var battery = new WindowsBatteryProvider();
        using var telemetry = new WindowsTelemetryProvider();
        var stateStore = new JsonStateStore();
        var control = new LenovoHardwareControlProvider(battery, telemetry, stateStore);
        var wmi = new LenovoWmiProvider();

        var capReport = await control.ProbeCapabilitiesAsync(allowElevation: false).ConfigureAwait(false);
        var thermal = wmi.ReadLiveThermalSnapshotDirect();
        var graphics = wmi.ReadGraphicsStatusDirect(null);
        IReadOnlyDictionary<string, (int Min, int Max, int Step, int Default)>? bounds = null;
        string? boundsError = null;
        try
        {
            bounds = wmi.ReadLiveFieldBoundsDirect();
        }
        catch (Exception ex)
        {
            boundsError = ex.Message;
        }

        var payload = new
        {
            CapabilityReport = capReport,
            LiveThermal = thermal,
            Graphics = graphics,
            LiveFieldBounds = bounds,
            BoundsError = boundsError
        };

        var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(payload, PrettyJsonOptions)).ConfigureAwait(false);
        return 0;
    }

    private static async Task<ElevatedResponse> HandleRequestAsync(ElevatedRequest req)
    {
        var wmi = new LenovoWmiProvider();
        var machine = wmi.ReadMachineIdentity();
        var battery = new WindowsBatteryProvider();
        using var telemetry = new WindowsTelemetryProvider();
        var stateStore = new JsonStateStore();
        var control = new LenovoHardwareControlProvider(battery, telemetry, stateStore);

        if (IsWriteOperation(req.Operation) && !machine.IsTargetLegion83JJ)
        {
            return new ElevatedResponse
            {
                Success = false,
                ErrorCode = "UNSUPPORTED_MACHINE",
                Message = $"Blocked hardware write on unverified machine '{machine.Manufacturer} {machine.Model}'."
            };
        }

        return req.Operation switch
        {
            ElevatedOperation.ProbeCapabilities => new ElevatedResponse
            {
                Success = true,
                Message = "Elevated capability probe completed.",
                CapabilityReport = await control.ProbeCapabilitiesAsync(allowElevation: false).ConfigureAwait(false),
                ThermalSnapshot = wmi.ReadLiveThermalSnapshotDirect(),
                GraphicsStatus = wmi.ReadGraphicsStatusDirect(null)
            },

            ElevatedOperation.ReadThermalSnapshot => new ElevatedResponse
            {
                Success = true,
                ThermalSnapshot = wmi.ReadLiveThermalSnapshotDirect()
            },

            ElevatedOperation.SetThermalMode => HandleSetThermalMode(wmi, req.ThermalMode),

            ElevatedOperation.ApplyPowerFields => HandleApplyPowerFields(wmi, req.PowerSettings),

            ElevatedOperation.ReadFanTables => HandleReadFanTables(wmi),

            ElevatedOperation.ApplyFanTables => new ElevatedResponse
            {
                Success = false,
                ErrorCode = "FAN_WRITE_NOT_VALIDATED",
                Message = "Fan table writes remain disabled until target curves and firmware payload are verified."
            },

            ElevatedOperation.RestoreDefaultFanTables => HandleRestoreDefaultFanTables(wmi),

            ElevatedOperation.ReadGraphicsMode => new ElevatedResponse
            {
                Success = true,
                GraphicsStatus = wmi.ReadGraphicsStatusDirect(null)
            },

            ElevatedOperation.SetGraphicsMode => HandleSetGraphicsMode(wmi, req.GraphicsMode),

            ElevatedOperation.ReadBatteryConservation => await HandleReadBatteryAsync(battery).ConfigureAwait(false),

            ElevatedOperation.SetBatteryConservation => await HandleSetBatteryAsync(battery, req.ConservationEnabled).ConfigureAwait(false),

            _ => new ElevatedResponse
            {
                Success = false,
                ErrorCode = "UNKNOWN_OPERATION",
                Message = $"Rejected unsupported elevated operation '{req.Operation}'."
            }
        };
    }

    private static bool IsWriteOperation(ElevatedOperation op) =>
        op is ElevatedOperation.SetThermalMode
            or ElevatedOperation.ApplyPowerFields
            or ElevatedOperation.ApplyFanTables
            or ElevatedOperation.RestoreDefaultFanTables
            or ElevatedOperation.SetGraphicsMode
            or ElevatedOperation.SetBatteryConservation;

    private static ElevatedResponse HandleSetThermalMode(LenovoWmiProvider wmi, ThermalMode? mode)
    {
        if (mode is null || mode == ThermalMode.Unknown)
        {
            return new ElevatedResponse { Success = false, ErrorCode = "INVALID_MODE", Message = "Missing or invalid thermal mode." };
        }

        try
        {
            var ok = wmi.SetThermalModeDirect(mode.Value, out var msg);
            return new ElevatedResponse
            {
                Success = ok,
                ErrorCode = ok ? "OK" : "READBACK_MISMATCH",
                Message = msg,
                ThermalSnapshot = wmi.ReadLiveThermalSnapshotDirect()
            };
        }
        catch (Exception ex)
        {
            return new ElevatedResponse { Success = false, ErrorCode = "WMI_ERROR", Message = ex.Message };
        }
    }

    private static ElevatedResponse HandleApplyPowerFields(LenovoWmiProvider wmi, ProfilePowerSettings? settings)
    {
        if (settings is null)
        {
            return new ElevatedResponse { Success = false, ErrorCode = "MISSING_PAYLOAD", Message = "Missing power settings payload." };
        }

        try
        {
            var liveBounds = wmi.ReadLiveFieldBoundsDirect();
            var contracts = ProfileSeedFactory.CreateDefaultFieldContracts()
                .Select(c => liveBounds.TryGetValue(c.FieldKey, out var b)
                    ? c with { MinValue = b.Min, MaxValue = b.Max, Step = b.Step, FirmwareDefault = b.Default, IsVerifiedOnTarget = true }
                    : c)
                .ToList();

            var validator = new ProfileValidationService();
            var validation = validator.ValidatePowerFields(settings, contracts, isOnAcPower: true, requireVerifiedContracts: true);
            if (!validation.IsValid)
            {
                return new ElevatedResponse
                {
                    Success = false,
                    ErrorCode = "BOUNDARY_VALIDATION_FAILED",
                    Message = string.Join(" ", validation.Errors),
                    FailedFields = validation.MissingFields
                };
            }

            var (applied, failed, msg) = wmi.ApplyPowerFieldsDirect(validation.ConvertedBackendValues);
            var readBack = wmi.ReadLivePowerValuesDirect();
            return new ElevatedResponse
            {
                Success = failed.Count == 0,
                ErrorCode = failed.Count == 0 ? "OK" : "FIELD_WRITE_FAILED",
                Message = msg,
                AppliedFields = applied,
                FailedFields = failed,
                ReadBackPowerValues = readBack.AsFieldMap()
            };
        }
        catch (Exception ex)
        {
            return new ElevatedResponse { Success = false, ErrorCode = "WMI_ERROR", Message = ex.Message };
        }
    }

    private static ElevatedResponse HandleReadFanTables(LenovoWmiProvider wmi)
    {
        try
        {
            var curve = wmi.ReadFanCurveDirect();
            return new ElevatedResponse { Success = true, FanCurve = curve, Message = curve.SourceDescription };
        }
        catch (Exception ex)
        {
            return new ElevatedResponse { Success = false, ErrorCode = "FAN_READ_ERROR", Message = ex.Message };
        }
    }

    private static ElevatedResponse HandleRestoreDefaultFanTables(LenovoWmiProvider wmi)
    {
        try
        {
            var ok = wmi.SetThermalModeDirect(ThermalMode.Balanced, out var msg);
            var curve = wmi.ReadFanCurveDirect();
            return new ElevatedResponse
            {
                Success = ok,
                ErrorCode = ok ? "OK" : "RESTORE_FAILED",
                Message = $"Restored Balanced thermal policy ({msg}).",
                FanCurve = curve
            };
        }
        catch (Exception ex)
        {
            return new ElevatedResponse { Success = false, ErrorCode = "FAN_RESTORE_ERROR", Message = ex.Message };
        }
    }

    private static ElevatedResponse HandleSetGraphicsMode(LenovoWmiProvider wmi, GraphicsRoutingMode? target)
    {
        if (target is null || target == GraphicsRoutingMode.Unknown)
        {
            return new ElevatedResponse { Success = false, ErrorCode = "INVALID_GRAPHICS_MODE", Message = "Missing graphics mode." };
        }

        try
        {
            var current = wmi.ReadGraphicsStatusDirect(null);
            if (!current.IsVerifiedLiveRead)
            {
                return new ElevatedResponse { Success = false, ErrorCode = "GRAPHICS_PROBE_FAILED", Message = current.StatusNote };
            }

            if (current.EffectiveMode == target.Value)
            {
                return new ElevatedResponse { Success = true, Message = "Effective graphics mode already matches target.", GraphicsStatus = current };
            }

            if (!current.SupportedModes.Any(m => m.Mode == target.Value))
            {
                return new ElevatedResponse { Success = false, ErrorCode = "UNSUPPORTED_GRAPHICS_MODE", Message = $"Mode {target.Value} is not supported on this machine." };
            }

            using var searcher = new ManagementObjectSearcher(LenovoWmiConstants.WmiNamespace, $"SELECT * FROM {LenovoWmiConstants.GameZoneDataClass}");
            foreach (ManagementObject instance in searcher.Get())
            {
                using (instance)
                {
                    if (target.Value is GraphicsRoutingMode.DgpuOnly or GraphicsRoutingMode.Hybrid)
                    {
                        using var inParams = instance.GetMethodParameters("SetGSyncStatus");
                        inParams["Data"] = target.Value == GraphicsRoutingMode.DgpuOnly ? 0u : 1u;
                        instance.InvokeMethod("SetGSyncStatus", inParams, null);
                    }

                    if (target.Value is GraphicsRoutingMode.HybridIgpuOnly or GraphicsRoutingMode.HybridAuto or GraphicsRoutingMode.Hybrid)
                    {
                        using var igpuIn = instance.GetMethodParameters("SetIGPUModeStatus");
                        igpuIn["mode"] = target.Value switch
                        {
                            GraphicsRoutingMode.HybridIgpuOnly => 1u,
                            GraphicsRoutingMode.HybridAuto => 2u,
                            _ => 0u
                        };
                        instance.InvokeMethod("SetIGPUModeStatus", igpuIn, null);
                    }

                    break;
                }
            }

            var after = wmi.ReadGraphicsStatusDirect(target.Value);
            return new ElevatedResponse
            {
                Success = true,
                Message = "Graphics mode request submitted to firmware. Restart Windows to verify effective hardware routing.",
                GraphicsStatus = after
            };
        }
        catch (Exception ex)
        {
            return new ElevatedResponse { Success = false, ErrorCode = "GRAPHICS_SET_ERROR", Message = ex.Message };
        }
    }

    private static async Task<ElevatedResponse> HandleReadBatteryAsync(WindowsBatteryProvider battery)
    {
        var status = await battery.ReadStatusAsync().ConfigureAwait(false);
        return new ElevatedResponse
        {
            Success = status.IsConservationSupported,
            BatteryChargeMode = status.LenovoChargeMode,
            Message = status.ProviderNote
        };
    }

    private static async Task<ElevatedResponse> HandleSetBatteryAsync(WindowsBatteryProvider battery, bool? enabled)
    {
        if (enabled is null)
        {
            return new ElevatedResponse { Success = false, ErrorCode = "MISSING_FLAG", Message = "Missing conservation enabled flag." };
        }

        var res = await battery.SetConservationModeAsync(enabled.Value).ConfigureAwait(false);
        return new ElevatedResponse
        {
            Success = res.Success,
            ErrorCode = res.ErrorCode,
            BatteryChargeMode = res.Value,
            Message = res.Message
        };
    }
}
