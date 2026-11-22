using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
        Converters = { new JsonStringEnumConverter() }
    };

    public Task<ElevatedResponse> ExecuteRequestAsync(ElevatedRequest requestWithoutToken, CancellationToken ct = default) =>
        ExecuteInternalAsync(requestWithoutToken, ct);

    private async Task<ElevatedResponse> ExecuteInternalAsync(ElevatedRequest templateRequest, CancellationToken ct)
    {
        var workerPath = Path.Combine(AppContext.BaseDirectory, "Utility.ElevatedWorker.exe");
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
                Arguments = $"--pipe {pipeName} --token {authToken}",
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

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(25));

        try
        {
            await pipeServer.WaitForConnectionAsync(timeoutCts.Token).ConfigureAwait(false);
            var payload = templateRequest with { AuthToken = authToken };
            var reqJson = JsonSerializer.Serialize(payload, JsonOptions);

            using var writer = new StreamWriter(pipeServer, new UTF8Encoding(false), leaveOpen: true);
            await writer.WriteLineAsync(reqJson.AsMemory(), timeoutCts.Token).ConfigureAwait(false);
            await writer.FlushAsync(timeoutCts.Token).ConfigureAwait(false);

            using var reader = new StreamReader(pipeServer, Encoding.UTF8, leaveOpen: true);
            var respJson = await reader.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false);
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

    private static PipeSecurity CreateRestrictedPipeSecurity()
    {
        var security = new PipeSecurity();
        var currentUser = WindowsIdentity.GetCurrent().User;
        if (currentUser is not null)
        {
            security.AddAccessRule(new PipeAccessRule(currentUser, PipeAccessRights.FullControl, AccessControlType.Allow));
        }

        var adminGroup = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        security.AddAccessRule(new PipeAccessRule(adminGroup, PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }
}
