using System.IO.Pipes;
using System.Security.Principal;
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
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    private static async Task<int> Main(string[] args)
    {
        if (!LenovoWmiProvider.IsProcessElevated())
        {
            return 5;
        }

        if (args is ["--enable-hardware-task"] or ["--remove-installation-hardware-tasks"])
        {
            try
            {
                if (args[0] == "--enable-hardware-task")
                    HardwareAuthorizationTask.Register();
                else
                    HardwareAuthorizationTask.RemoveForCurrentInstallation();

                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 5;
            }
        }

        var scheduled = args.Length > 0 && args[0] == "--hardware-task";
        if (scheduled)
            args = args[1..];

        if (args.Length != 6 || args[0] != "--pipe" || args[2] != "--token" || args[4] != "--server-pid" ||
            !args[1].StartsWith("UtilityWorker_", StringComparison.Ordinal) ||
            !Guid.TryParseExact(args[1].AsSpan("UtilityWorker_".Length), "N", out _) ||
            args[3].Length != 64 || !args[3].All(Uri.IsHexDigit) ||
            !uint.TryParse(args[5], out var serverPid) || serverPid == 0)
        {
            return 87;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut,
                PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            var appPath = scheduled ? Path.Combine(AppContext.BaseDirectory, "Utility.exe") : null;
            if (!ElevatedWorkerClient.IsExpectedServer(pipe, serverPid, appPath))
            {
                return 5;
            }

            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            var line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            var request = line is null ? null : JsonSerializer.Deserialize<ElevatedRequest>(line, JsonOptions);
            if (request is null || !string.Equals(request.AuthToken, args[3], StringComparison.Ordinal) ||
                (scheduled && !HardwareAuthorizationTask.Allows(request.Operation)))
            {
                return 5;
            }

            if (request.Operation == ElevatedOperation.MonitorFirmwareSensors)
            {
                using var monitorWriter = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true);
                var provider = new LenovoWmiProvider();
                using var sensors = new LenovoWmiProvider.FirmwareSensorReader();
                var nextThermal = DateTimeOffset.MaxValue;
                var firstSample = true;
                while (await reader.ReadLineAsync().ConfigureAwait(false) == "sample")
                {
                    var sample = sensors.Read() ?? new FirmwareSensorSample(null, null, null);
                    // Publish the first temperature before querying the ten slower profile fields.
                    var thermal = DateTimeOffset.UtcNow >= nextThermal ? provider.ReadLiveThermalSnapshotDirect() : null;
                    if (firstSample || thermal is not null)
                        nextThermal = firstSample ? DateTimeOffset.MinValue : DateTimeOffset.UtcNow.AddSeconds(10);
                    var sampleResponse = new ElevatedResponse
                    {
                        Success = true,
                        FirmwareSensors = sample,
                        ThermalSnapshot = thermal
                    };
                    firstSample = false;
                    await monitorWriter.WriteLineAsync(JsonSerializer.Serialize(sampleResponse, JsonOptions)).ConfigureAwait(false);
                    await monitorWriter.FlushAsync().ConfigureAwait(false);
                }
                return 0;
            }

            ElevatedResponse response;
            if (request.Operation == ElevatedOperation.RunCoolingCycle)
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(55));
                using var stop = new CancellationTokenSource();
                using var cycleComplete = new CancellationTokenSource();
                var watch = WatchCoolingConnectionAsync(reader, stop, cycleComplete.Token);
                try
                {
                    response = await HandleAsync(request, stop.Token).ConfigureAwait(false);
                }
                finally
                {
                    await cycleComplete.CancelAsync().ConfigureAwait(false);
                    await watch.ConfigureAwait(false);
                }
            }
            else
            {
                if (request.Operation == ElevatedOperation.InstallLenovoDriver)
                    timeout.CancelAfter(TimeSpan.FromMinutes(40));
                if (request.Operation == ElevatedOperation.CleanupLenovoDrivers)
                    timeout.CancelAfter(TimeSpan.FromSeconds(55));
                response = await HandleAsync(request, timeout.Token).ConfigureAwait(false);
            }
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions).AsMemory(), timeout.Token)
                .ConfigureAwait(false);
            await writer.FlushAsync(timeout.Token).ConfigureAwait(false);
            return response.Success ? 0 : 2;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or OperationCanceledException)
        {
            return 1;
        }
    }

    private static async Task WatchCoolingConnectionAsync(StreamReader reader, CancellationTokenSource stop,
        CancellationToken cycleComplete)
    {
        try
        {
            // The only follow-up message is a stop request. EOF also ends the cycle.
            _ = await reader.ReadLineAsync(cycleComplete).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cycleComplete.IsCancellationRequested)
        {
            return;
        }
        catch (IOException)
        {
        }
        await stop.CancelAsync().ConfigureAwait(false);
    }

    private static async Task<ElevatedResponse> HandleAsync(ElevatedRequest request, CancellationToken ct)
    {
        try
        {
            var wmi = new LenovoWmiProvider();
            switch (request.Operation)
            {
                case ElevatedOperation.ProbeCapabilities:
                    using (var telemetry = new WindowsTelemetryProvider())
                    {
                        var control = new LenovoHardwareControlProvider(new WindowsBatteryProvider(), telemetry);
                        return new ElevatedResponse
                        {
                            Success = true,
                            CapabilityReport = await control.ProbeCapabilitiesAsync(allowElevation: false, ct).ConfigureAwait(false)
                        };
                    }

                case ElevatedOperation.ReadThermalSnapshot:
                    return new ElevatedResponse { Success = true, ThermalSnapshot = wmi.ReadLiveThermalSnapshotDirect() };

                case ElevatedOperation.ReadFanTables:
                    var curve = wmi.ReadFanCurveDirect();
                    return new ElevatedResponse { Success = true, FanCurve = curve, Message = curve.SourceDescription };

                case ElevatedOperation.ReadGraphicsMode:
                    return new ElevatedResponse { Success = true, GraphicsStatus = wmi.ReadGraphicsStatusDirect() };

                case ElevatedOperation.SetGraphicsMode:
                    var graphics = wmi.SetGraphicsModeDirect(request.GraphicsMode);
                    return new ElevatedResponse
                    {
                        Success = graphics.Success,
                        ErrorCode = graphics.ErrorCode,
                        Message = graphics.Message,
                        GraphicsStatus = graphics.Value
                    };

                case ElevatedOperation.SetFanSpeedLevels:
                    var fanLevels = wmi.SetFanSpeedLevelsDirect(request.FanLevels);
                    return new ElevatedResponse
                    {
                        Success = fanLevels.Success,
                        ErrorCode = fanLevels.ErrorCode,
                        Message = fanLevels.Message,
                        FanCurve = fanLevels.Value
                    };

                case ElevatedOperation.ApplyOwnerPowerPreset:
                    var applied = wmi.ApplyOwnerPowerPresetDirect(request.OwnerProfileId);
                    return new ElevatedResponse
                    {
                        Success = applied.Success,
                        ErrorCode = applied.ErrorCode,
                        Message = applied.Message,
                        ThermalSnapshot = applied.Value
                    };

                case ElevatedOperation.RunCoolingCycle:
                    var cooled = await Task.Run(() => wmi.RunCoolingCycleDirect(ct)).ConfigureAwait(false);
                    return new ElevatedResponse
                    {
                        Success = cooled.Success,
                        WasCancelled = cooled.WasCancelledByUser,
                        ErrorCode = cooled.ErrorCode,
                        Message = cooled.Message,
                        CoolingCycle = cooled.Value
                    };

                case ElevatedOperation.ListRestorePoints:
                    return new ElevatedResponse { Success = true, RestorePoints = SystemRestoreService.ListDirect() };

                case ElevatedOperation.DeleteRestorePoints:
                    var (removedPoints, restoreErrors) = SystemRestoreService.DeleteDirect(request.RestorePointSequenceNumbers);
                    return new ElevatedResponse
                    {
                        Success = restoreErrors.Count == 0,
                        ErrorCode = restoreErrors.Count == 0 ? null : "RESTORE_POINT_DELETE_PARTIAL",
                        Message = $"{removedPoints.Count} removed" + (restoreErrors.Count == 0 ? string.Empty : "; " + string.Join("; ", restoreErrors)),
                        RestorePoints = SystemRestoreService.ListDirect()
                    };

                case ElevatedOperation.SetStartupEntryEnabled when request.StartupEnabled is bool startupEnabled:
                    var toggled = WindowsSoftwareProvider.SetStartupEntryEnabledDirect(request.StartupEntryId, startupEnabled);
                    return new ElevatedResponse { Success = toggled.Success, ErrorCode = toggled.ErrorCode, Message = toggled.Message };

                case ElevatedOperation.InstallLenovoDriver when request.LenovoPackageId is { } id &&
                                                               request.LenovoManifestHash is { } manifestHash:
                    var installation = await new LenovoDriverUpdates().InstallInWorkerAsync(id, manifestHash, ct).ConfigureAwait(false);
                    return new ElevatedResponse { Success = true, LenovoDriverInstall = installation };

                case ElevatedOperation.CleanupLenovoDrivers:
                    var cleanup = await LenovoDriverUpdates.CleanupPreviousDriversInWorkerAsync(ct).ConfigureAwait(false);
                    return new ElevatedResponse { Success = true, LenovoDriverCleanup = cleanup };

                default:
                    return new ElevatedResponse { ErrorCode = "UNKNOWN_OPERATION", Message = "Unknown Utility operation." };
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ElevatedResponse { ErrorCode = "PROBE_FAILED", Message = ex.Message };
        }
    }
}
