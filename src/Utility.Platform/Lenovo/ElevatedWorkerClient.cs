using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using Utility.Core.Models;

namespace Utility.Platform.Lenovo;

/// <summary>
/// Launches the trusted installed Utility.ElevatedWorker.exe on demand over an ACL-restricted named pipe.
/// Handles UAC cancellation (Win32 ERROR_CANCELLED = 1223) without modifying state.
/// </summary>
public sealed class ElevatedWorkerClient
{
    private const int ErrorCancelled = 1223;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public Task<ElevatedResponse> ExecuteRequestAsync(ElevatedRequest requestWithoutToken, CancellationToken ct = default) =>
        ExecuteInternalAsync(requestWithoutToken, ct);

    public Task<ElevatedResponse> MonitorFirmwareSensorsAsync(Func<FirmwareSensorSample, LiveThermalSnapshot?, Task> receive,
        Func<CancellationToken, Task> waitForNextSample, CancellationToken ct) =>
        ExecuteInternalAsync(new ElevatedRequest
        {
            AuthToken = string.Empty,
            Operation = ElevatedOperation.MonitorFirmwareSensors
        }, ct, async (reader, writer) =>
        {
            while (true)
            {
                using var sampleTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                sampleTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                await writer.WriteLineAsync("sample".AsMemory(), sampleTimeout.Token).ConfigureAwait(false);
                await writer.FlushAsync(sampleTimeout.Token).ConfigureAwait(false);
                var line = await reader.ReadLineAsync(sampleTimeout.Token).ConfigureAwait(false);
                var response = line is null ? null : JsonSerializer.Deserialize<ElevatedResponse>(line, JsonOptions);
                if (response?.FirmwareSensors is not { } sample)
                    return response ?? new ElevatedResponse { ErrorCode = "SENSOR_WORKER_CLOSED", Message = "Sensor worker disconnected." };

                await receive(sample, response.ThermalSnapshot).ConfigureAwait(false);
                await waitForNextSample(ct).ConfigureAwait(false);
            }
        });

    private async Task<ElevatedResponse> ExecuteInternalAsync(ElevatedRequest templateRequest, CancellationToken ct,
        Func<StreamReader, StreamWriter, Task<ElevatedResponse>>? monitor = null)
    {
        var workerPath = Path.Combine(AppContext.BaseDirectory, "Utility.ElevatedWorker.exe");
        if (!Enum.IsDefined(templateRequest.Operation))
        {
            return new ElevatedResponse { ErrorCode = "UNKNOWN_OPERATION", Message = "Unsupported worker operation." };
        }

        if (!File.Exists(workerPath))
        {
            return new ElevatedResponse
            {
                Success = false,
                ErrorCode = "WORKER_NOT_FOUND",
                Message = $"Trusted elevated worker binary was not found at '{workerPath}'."
            };
        }

        var pipeName = $"UtilityWorker_{Guid.NewGuid():N}";
        var authToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var pipeSecurity = CreateRestrictedPipeSecurity();

        await using var pipeServer = NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 65536,
            outBufferSize: 65536,
            pipeSecurity);

        Process? proc = null;
        try
        {
            proc = Process.Start(new ProcessStartInfo
            {
                FileName = workerPath,
                Arguments = $"--pipe {pipeName} --token {authToken} --server-pid {Environment.ProcessId}",
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }

        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return new ElevatedResponse
            {
                Success = false,
                WasCancelled = true,
                ErrorCode = "USER_CANCELLED",
                Message = "UAC elevation prompt was cancelled by the user; hardware state remains unchanged."
            };
        }
        catch (Exception ex)
        {
            return new ElevatedResponse
            {
                Success = false,
                ErrorCode = "ELEVATION_START_FAILED",
                Message = $"Failed to launch elevated worker: {ex.Message}"
            };
        }

        if (proc is null)
        {
            return new ElevatedResponse { ErrorCode = "ELEVATION_START_FAILED", Message = "Windows did not start the probe worker." };
        }

        var cooling = templateRequest.Operation == ElevatedOperation.RunCoolingCycle;
        using var timeoutCts = cooling ? new CancellationTokenSource() : CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(cooling ? 65 : 25));

        try
        {
            await pipeServer.WaitForConnectionAsync(timeoutCts.Token).ConfigureAwait(false);
            if (!GetNamedPipeClientProcessId(pipeServer.SafePipeHandle, out var clientPid) || clientPid != (uint)proc.Id)
            {
                return new ElevatedResponse { ErrorCode = "UNEXPECTED_WORKER", Message = "The pipe client did not match the launched worker." };
            }
            var payload = templateRequest with { AuthToken = authToken };
            var reqJson = JsonSerializer.Serialize(payload, JsonOptions);

            using var writer = new StreamWriter(pipeServer, new UTF8Encoding(false), leaveOpen: true);
            await writer.WriteLineAsync(reqJson.AsMemory(), timeoutCts.Token).ConfigureAwait(false);
            await writer.FlushAsync(timeoutCts.Token).ConfigureAwait(false);

            using var reader = new StreamReader(pipeServer, Encoding.UTF8, leaveOpen: true);
            if (monitor is not null)
                return await monitor(reader, writer).ConfigureAwait(false);

            using var responseComplete = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token);
            var stopTask = cooling ? SendCoolingStopAsync(writer, ct, responseComplete.Token) : Task.CompletedTask;
            string? respJson;
            try
            {
                respJson = await reader.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            finally
            {
                await responseComplete.CancelAsync().ConfigureAwait(false);
                await stopTask.ConfigureAwait(false);
            }
            if (string.IsNullOrWhiteSpace(respJson))
            {
                return new ElevatedResponse
                {
                    Success = false,
                    ErrorCode = "EMPTY_WORKER_RESPONSE",
                    Message = "Elevated worker closed the pipe without returning a response."
                };
            }

            return JsonSerializer.Deserialize<ElevatedResponse>(respJson, JsonOptions)
                   ?? new ElevatedResponse { Success = false, ErrorCode = "INVALID_WORKER_RESPONSE", Message = "Invalid elevated worker JSON response." };
        }
        catch (OperationCanceledException) when (monitor is not null && ct.IsCancellationRequested)
        {
            return new ElevatedResponse { Success = true };
        }
        catch (OperationCanceledException)
        {
            return new ElevatedResponse
            {
                Success = false,
                ErrorCode = "ELEVATION_TIMEOUT",
                Message = "Timed out waiting for elevated worker IPC response."
            };
        }
        catch (Exception ex)
        {
            return new ElevatedResponse
            {
                Success = false,
                ErrorCode = "IPC_ERROR",
                Message = $"Elevated worker communication failed: {ex.Message}"
            };
        }
        finally
        {
            // The read-only worker exits on pipe EOF, including shutdown during a sample.
            pipeServer.Close();
            if (proc is not null)
            {
                try
                {
                    await proc.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch
                {
                    // Ignore wait timeout on cleanup.
                }

                proc.Dispose();
            }
        }
    }

    private static async Task SendCoolingStopAsync(StreamWriter writer, CancellationToken stop,
        CancellationToken responseComplete)
    {
        using var either = CancellationTokenSource.CreateLinkedTokenSource(stop, responseComplete);
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, either.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!stop.IsCancellationRequested || responseComplete.IsCancellationRequested)
                return;
        }

        try
        {
            await writer.WriteLineAsync("stop".AsMemory(), responseComplete).ConfigureAwait(false);
            await writer.FlushAsync(responseComplete).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            // The worker also stops when the connection closes or the cycle limit is reached.
        }
    }

    private static PipeSecurity CreateRestrictedPipeSecurity()
    {
        var security = new PipeSecurity();
        using var identity = WindowsIdentity.GetCurrent();
        var currentUser = identity.User;
        if (currentUser is not null)
        {
            security.AddAccessRule(new PipeAccessRule(currentUser, PipeAccessRights.FullControl, AccessControlType.Allow));
        }

        var adminGroup = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        security.AddAccessRule(new PipeAccessRule(adminGroup, PipeAccessRights.FullControl, AccessControlType.Allow));
        var network = new SecurityIdentifier(WellKnownSidType.NetworkSid, null);
        security.AddAccessRule(new PipeAccessRule(network, PipeAccessRights.FullControl, AccessControlType.Deny));
        return security;
    }

    public static bool IsExpectedServer(NamedPipeClientStream pipe, uint expectedProcessId) =>
        GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var processId) && processId == expectedProcessId;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
}
