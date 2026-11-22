using System.Management;
using System.Runtime.InteropServices;
using Utility.Core.Models;

namespace Utility.Platform.Providers;

/// <summary>
/// Lists and removes Windows System Restore points. Both WMI root\default:SystemRestore and
/// SRRemoveRestorePoint require administrator rights, so this runs inside Utility.ElevatedWorker only.
/// </summary>
public static class SystemRestoreService
{
    public static RestorePointList ListDirect()
    {
        var points = new List<RestorePointInfo>();
        using (var searcher = new ManagementObjectSearcher(@"root\default",
                   "SELECT SequenceNumber, Description, CreationTime FROM SystemRestore"))
        {
            foreach (ManagementObject item in searcher.Get())
            {
                using (item)
                {
                    var sequence = Convert.ToUInt32(item["SequenceNumber"]);
                    var description = item["Description"] as string ?? string.Empty;
                    var created = item["CreationTime"] is string dmtf
                        ? ManagementDateTimeConverter.ToDateTime(dmtf).ToUniversalTime()
                        : DateTime.MinValue;
                    points.Add(new RestorePointInfo(sequence, description, created));
                }
            }
        }

        return new RestorePointList(points.OrderByDescending(point => point.CreatedUtc).ToArray(), ReadSystemDriveShadowUsage());
    }

    /// <summary>
    /// Deletes only sequence numbers that exist right now; anything else is rejected.
    /// Returns the numbers removed and per-point errors.
    /// </summary>
    public static (IReadOnlyList<uint> Removed, IReadOnlyList<string> Errors) DeleteDirect(IReadOnlyCollection<uint>? requested)
    {
        var removed = new List<uint>();
        var errors = new List<string>();
        if (requested is not { Count: > 0 })
            return (removed, errors);

        var existing = ListDirect().Points.Select(point => point.SequenceNumber).ToHashSet();
        foreach (var sequence in requested.Distinct())
        {
            if (!existing.Contains(sequence))
            {
                errors.Add($"#{sequence}: not found");
                continue;
            }

            var status = SRRemoveRestorePoint(sequence);
            if (status == 0)
                removed.Add(sequence);
            else
                errors.Add($"#{sequence}: error {status}");
        }

        return (removed, errors);
    }

    /// <summary>Used shadow-copy storage on the system drive, or null when it cannot be read.</summary>
    private static long? ReadSystemDriveShadowUsage()
    {
        try
        {
            var systemRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            string? volumeId = null;
            using (var volumes = new ManagementObjectSearcher(@"root\cimv2", "SELECT DeviceID, DriveLetter FROM Win32_Volume"))
            {
                foreach (ManagementObject volume in volumes.Get())
                {
                    using (volume)
                    {
                        if (volume["DriveLetter"] is string letter &&
                            systemRoot?.StartsWith(letter, StringComparison.OrdinalIgnoreCase) == true)
                            volumeId = volume["DeviceID"] as string;
                    }
                }
            }

            if (volumeId is null)
                return null;

            long used = 0;
            using var storage = new ManagementObjectSearcher(@"root\cimv2", "SELECT UsedSpace, Volume FROM Win32_ShadowStorage");
            foreach (ManagementObject item in storage.Get())
            {
                using (item)
                {
                    // Volume is a reference path like Win32_Volume.DeviceID="\\\\?\\Volume{...}\\".
                    if (item["Volume"] is string reference &&
                        reference.Replace(@"\\", @"\").Contains(volumeId, StringComparison.OrdinalIgnoreCase))
                        used += Convert.ToInt64(item["UsedSpace"]);
                }
            }

            return used;
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException)
        {
            return null;
        }
    }

    [DllImport("srclient.dll")]
    private static extern int SRRemoveRestorePoint(uint dwRPNum);
}
