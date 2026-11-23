using System.Management;
using Utility.Core.Models;
using Utility.Core.Services;
using Utility.Platform.Native;

namespace Utility.Platform.Providers;

/// <summary>
/// Reads installed PnP signed drivers and inspects local package files.
/// </summary>
public sealed class WindowsDriverProvider : IDriverProvider
{
    public Task<IReadOnlyList<InstalledDriverEntry>> ListDriversAsync(CancellationToken ct = default)
    {
        return Task.Run<IReadOnlyList<InstalledDriverEntry>>(() =>
        {
            List<InstalledDriverEntry> results;
            try
            {
                results = DeviceDriverInventory.Read(ClassifyDriver, ct);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                results = ReadWmiDrivers(ct);
            }

            return results
                .OrderBy(d => d.Category)
                .ThenBy(d => d.DeviceName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }, ct);
    }

    private static List<InstalledDriverEntry> ReadWmiDrivers(CancellationToken ct)
    {
        var results = new List<InstalledDriverEntry>();
        using var searcher = new ManagementObjectSearcher(
            @"root\cimv2",
            "SELECT DeviceName, DriverProviderName, DriverVersion, DriverDate, InfName, HardWareID, DeviceClass FROM Win32_PnPSignedDriver");

        using var items = searcher.Get();
        foreach (ManagementObject item in items)
        {
            using var obj = item;
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

        return results;
    }

    public Task<DriverPackageInspection> InspectLocalPackageAsync(string filePath, CancellationToken ct = default) =>
        Task.Run(() => WinTrustInterop.InspectLocalFile(filePath), ct);

    internal static DriverCategory ClassifyDriver(string deviceName, string provider, string deviceClass)
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
