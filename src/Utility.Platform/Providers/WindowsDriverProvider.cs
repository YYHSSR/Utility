using System.Diagnostics;
using System.Management;
using Utility.Core.Models;
using Utility.Core.Services;
using Utility.Platform.Native;

namespace Utility.Platform.Providers;

/// <summary>
/// Reads installed PnP signed drivers via Win32_PnPSignedDriver, inspects local official driver packages
/// via WinVerifyTrust, and launches verified interactive installers only after byte-hash confirmation.
/// </summary>
public sealed class WindowsDriverProvider : IDriverProvider
{
    public Task<IReadOnlyList<InstalledDriverEntry>> ListDriversAsync(CancellationToken ct = default)
    {
        return Task.Run<IReadOnlyList<InstalledDriverEntry>>(() =>
        {
            var results = new List<InstalledDriverEntry>();
            using var searcher = new ManagementObjectSearcher(
                @"root\cimv2",
                "SELECT DeviceName, DriverProviderName, DriverVersion, DriverDate, InfName, HardWareID, DeviceClass FROM Win32_PnPSignedDriver");

            foreach (ManagementObject obj in searcher.Get())
            {
                ct.ThrowIfCancellationRequested();
                var deviceName = obj["DeviceName"]?.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(deviceName))
                {
                    continue;
                }

                var provider = obj["DriverProviderName"]?.ToString()?.Trim() ?? string.Empty;
                var version = obj["DriverVersion"]?.ToString()?.Trim() ?? string.Empty;
                var rawDate = obj["DriverDate"]?.ToString()?.Trim() ?? string.Empty;
                var formattedDate = FormatWmiDate(rawDate);
                var infName = obj["InfName"]?.ToString()?.Trim() ?? string.Empty;
                var hwId = obj["HardWareID"]?.ToString()?.Trim() ?? string.Empty;
                var devClass = obj["DeviceClass"]?.ToString()?.Trim() ?? string.Empty;

                var category = ClassifyDriver(deviceName, provider, devClass);
                results.Add(new InstalledDriverEntry(deviceName, provider, version, formattedDate, infName, hwId, category));
            }

            return results
                .OrderBy(d => d.Category)
                .ThenBy(d => d.DeviceName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }, ct);
    }

    public Task<DriverPackageInspection> InspectLocalPackageAsync(string filePath, CancellationToken ct = default) =>
        Task.Run(() => WinTrustInterop.InspectLocalFile(filePath), ct);

    public async Task<OperationResult<int>> LaunchVerifiedInstallerAsync(
        string filePath,
        string expectedSha256Hex,
        CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
        {
            return OperationResult<int>.Fail("FILE_NOT_FOUND", $"Driver package not found: {filePath}");
        }

        // Re-verify the exact file bytes and Authenticode trust immediately before launch.
        var fresh = await InspectLocalPackageAsync(filePath, ct).ConfigureAwait(false);
        if (!string.Equals(fresh.Sha256Hex, expectedSha256Hex, StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult<int>.Fail("HASH_CHANGED", "File contents changed after verification; launch aborted.");
        }

        if (!fresh.IsSignatureValid)
        {
            return OperationResult<int>.Fail("SIGNATURE_INVALID", $"Authenticode re-check failed: {fresh.SignatureStatusDetail}");
        }

        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = fresh.FilePath,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(fresh.FilePath) ?? Environment.CurrentDirectory
            });

            if (proc is null)
            {
                return OperationResult<int>.Fail("LAUNCH_FAILED", "Windows returned a null process handle.");
            }

            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            return OperationResult<int>.Ok(proc.ExitCode, $"Vendor installer exited with code {proc.ExitCode}. Refreshing driver inventory.");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return OperationResult<int>.Cancelled("Installer UAC prompt was cancelled by the user.");
        }
        catch (Exception ex)
        {
            return OperationResult<int>.Fail("LAUNCH_EXCEPTION", $"Failed to launch verified installer: {ex.Message}");
        }
    }

    public bool OpenExternalTarget(string urlOrSettingsUri)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = urlOrSettingsUri, UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static DriverCategory ClassifyDriver(string deviceName, string provider, string deviceClass)
    {
        var text = $"{deviceName} {provider} {deviceClass}";
        if (deviceName.Contains("Lenovo", StringComparison.OrdinalIgnoreCase) ||
            provider.Contains("Lenovo", StringComparison.OrdinalIgnoreCase))
        {
            return DriverCategory.LenovoControl;
        }

        if (deviceClass.Equals("Display", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("UHD Graphics", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
        {
            return DriverCategory.Graphics;
        }

        if (deviceClass.Equals("Net", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Wireless", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Ethernet", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase))
        {
            return DriverCategory.WirelessNetwork;
        }

        if (deviceClass.Equals("MEDIA", StringComparison.OrdinalIgnoreCase) ||
            deviceClass.Equals("AudioEndpoint", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Realtek", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Audio", StringComparison.OrdinalIgnoreCase))
        {
            return DriverCategory.Audio;
        }

        if (text.Contains("Chipset", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("PCI", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Management Engine", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Dynamic Tuning", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Serial IO", StringComparison.OrdinalIgnoreCase))
        {
            return DriverCategory.Chipset;
        }

        return DriverCategory.Other;
    }

    private static string FormatWmiDate(string raw)
    {
        if (raw.Length >= 8 &&
            int.TryParse(raw[..4], out var y) &&
            int.TryParse(raw.Substring(4, 2), out var m) &&
            int.TryParse(raw.Substring(6, 2), out var d))
        {
            return $"{y:D4}-{m:D2}-{d:D2}";
        }

        return raw;
    }
}
