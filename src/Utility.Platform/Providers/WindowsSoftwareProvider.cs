using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Utility.Core.Models;
using Utility.Core.Services;

namespace Utility.Platform.Providers;

/// <summary>
/// Read-only inventory for installed apps (Registry + AppX, never Win32_Product),
/// Windows startup entries, and on-demand Lenovo leftover scanning.
/// </summary>
public sealed class WindowsSoftwareProvider : ISoftwareInventoryProvider
{
    public Task<SoftwareInventorySnapshot> ScanAsync(bool includeLenovoLeftovers, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var now = DateTimeOffset.UtcNow;
            var installed = ReadInstalledApps();
            var startup = ReadStartupEntries();
            var leftovers = includeLenovoLeftovers ? ScanLenovoArtifactsReadOnly(installed) : [];
            return new SoftwareInventorySnapshot(installed, startup, leftovers, now);
        }, ct);
    }

    public bool OpenWindowsInstalledApps(string? packageFamilyName = null)
    {
        var uri = !string.IsNullOrWhiteSpace(packageFamilyName)
            ? $"ms-settings:appsfeatures-app?{packageFamilyName}"
            : "ms-settings:appsfeatures";

        if (TryLaunchUri(uri))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(packageFamilyName) && TryLaunchUri("ms-settings:appsfeatures");
    }

    public bool OpenWindowsStartupApps() => TryLaunchUri("ms-settings:startupapps");

    private static IReadOnlyList<InstalledAppEntry> ReadInstalledApps()
    {
        var map = new Dictionary<string, InstalledAppEntry>(StringComparer.OrdinalIgnoreCase);

        ReadUninstallHive(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", InstallScope.MachineX64, map);
        ReadUninstallHive(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", InstallScope.MachineX86, map);
        ReadUninstallHive(RegistryHive.CurrentUser, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", InstallScope.CurrentUser, map);
        ReadCurrentUserAppxPackages(map);

        return map.Values
            .OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ReadUninstallHive(
        RegistryHive hive,
        RegistryView view,
        string subKeyPath,
        InstallScope scope,
        Dictionary<string, InstalledAppEntry> map)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var uninstall = baseKey.OpenSubKey(subKeyPath);
        if (uninstall is null)
        {
            return;
        }

        foreach (var childName in uninstall.GetSubKeyNames())
        {
            using var sub = uninstall.OpenSubKey(childName);
            if (sub is null) continue;

            var systemComponent = sub.GetValue("SystemComponent");
            if (systemComponent is int sc && sc == 1) continue;

            var releaseType = sub.GetValue("ReleaseType")?.ToString();
            if (!string.IsNullOrWhiteSpace(releaseType)) continue;

            var name = sub.GetValue("DisplayName")?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;

            var publisher = sub.GetValue("Publisher")?.ToString()?.Trim() ?? string.Empty;
            var version = sub.GetValue("DisplayVersion")?.ToString()?.Trim() ?? string.Empty;
            var stableId = $"{scope}:{childName}";

            map.TryAdd(stableId, new InstalledAppEntry(
                StableId: stableId,
                DisplayName: name,
                Publisher: publisher,
                Version: version,
                Scope: scope,
                PackageFamilyName: null,
                SupportsWindowsAppsDeepLink: false));
        }
    }

    private static void ReadCurrentUserAppxPackages(Dictionary<string, InstalledAppEntry> map)
    {
        const string repoPath = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
        using var key = Registry.CurrentUser.OpenSubKey(repoPath);
        if (key is null)
        {
            return;
        }

        foreach (var pkgFullName in key.GetSubKeyNames())
        {
            using var pkgKey = key.OpenSubKey(pkgFullName);
            if (pkgKey is null) continue;

            var parts = pkgFullName.Split('_');
            if (parts.Length < 2) continue;

            var pkgName = parts[0];
            var version = parts[1];
            var publisherId = parts.Length >= 5 ? parts[^1] : string.Empty;
            var familyName = string.IsNullOrEmpty(publisherId) ? null : $"{pkgName}_{publisherId}";
            var displayName = pkgKey.GetValue("DisplayName")?.ToString();
            if (string.IsNullOrWhiteSpace(displayName) || displayName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
            {
                displayName = pkgName;
            }

            var stableId = $"StoreAppx:{pkgFullName}";
            map.TryAdd(stableId, new InstalledAppEntry(
                StableId: stableId,
                DisplayName: displayName,
                Publisher: pkgKey.GetValue("PackagePublisher")?.ToString() ?? string.Empty,
                Version: version,
                Scope: InstallScope.StoreAppx,
                PackageFamilyName: familyName,
                SupportsWindowsAppsDeepLink: familyName is not null));
        }
    }

    private static IReadOnlyList<StartupEntry> ReadStartupEntries()
    {
        var entries = new List<StartupEntry>();

        ReadRunKey(RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\Run",
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", StartupEntrySource.UserRunRegistry, "HKCU\\...\\Run", entries);
        ReadRunKey(RegistryHive.LocalMachine, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\Run",
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", StartupEntrySource.MachineRunRegistry, "HKLM\\...\\Run", entries);
        ReadRunKey(RegistryHive.LocalMachine, RegistryView.Registry32, @"Software\Microsoft\Windows\CurrentVersion\Run",
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32", StartupEntrySource.MachineRunRegistry, "HKLM(x86)\\...\\Run", entries);

        ReadStartupFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup), StartupEntrySource.UserStartupFolder, entries);
        ReadStartupFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), StartupEntrySource.MachineStartupFolder, entries);
        ReadLogonScheduledTasks(entries);

        return entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void ReadRunKey(
        RegistryHive hive,
        RegistryView view,
        string runPath,
        string approvedPath,
        StartupEntrySource source,
        string label,
        List<StartupEntry> target)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var runKey = baseKey.OpenSubKey(runPath);
        if (runKey is null) return;

        using var approvedKey = baseKey.OpenSubKey(approvedPath);
        foreach (var valueName in runKey.GetValueNames())
        {
            var cmd = runKey.GetValue(valueName)?.ToString();
            if (string.IsNullOrWhiteSpace(cmd)) continue;

            bool? enabled = null;
            if (approvedKey?.GetValue(valueName) is byte[] bytes && bytes.Length >= 1)
            {
                enabled = (bytes[0] & 0x01) == 0;
            }

            target.Add(new StartupEntry(
                StableId: $"{source}:{label}:{valueName}",
                Name: valueName,
                CommandOrTarget: cmd,
                Source: source,
                IsEnabled: enabled ?? true,
                Location: label));
        }
    }

    private static void ReadStartupFolder(string folderPath, StartupEntrySource source, List<StartupEntry> target)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(folderPath))
        {
            var name = Path.GetFileName(file);
            if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            target.Add(new StartupEntry(
                StableId: $"{source}:{file}",
                Name: Path.GetFileNameWithoutExtension(file),
                CommandOrTarget: file,
                Source: source,
                IsEnabled: true,
                Location: folderPath));
        }
    }

    private static void ReadLogonScheduledTasks(List<StartupEntry> target)
    {
        foreach (var task in EnumerateScheduledTasksSafe())
        {
            if (!task.HasLogonOrBootTrigger) continue;
            target.Add(new StartupEntry(
                StableId: $"Task:{task.TaskPath}{task.TaskName}",
                Name: task.TaskName,
                CommandOrTarget: task.ExecutePath ?? "(non-exec action)",
                Source: StartupEntrySource.ScheduledTask,
                IsEnabled: task.IsEnabled,
                Location: task.TaskPath));
        }
    }

    private static IReadOnlyList<LenovoLeftoverFinding> ScanLenovoArtifactsReadOnly(IReadOnlyList<InstalledAppEntry> installed)
    {
        var findings = new List<LenovoLeftoverFinding>();
        var isPcManagerInstalled = installed.Any(a =>
            a.StableId.Contains("{A9861883-31C5-4324-BD9A-DC9527EEB675}", StringComparison.OrdinalIgnoreCase) ||
            a.DisplayName.Contains("PC Manager", StringComparison.OrdinalIgnoreCase) ||
            a.DisplayName.Contains("PCManager", StringComparison.OrdinalIgnoreCase));

        var isLegionZoneInstalled = installed.Any(a =>
            a.StableId.Contains("LegionZone", StringComparison.OrdinalIgnoreCase) ||
            a.DisplayName.Contains("Legion Zone", StringComparison.OrdinalIgnoreCase));

        // 1. Scheduled Tasks mentioning Lenovo, Legion, PCManager, or SystemIdleMonitor.
        foreach (var task in EnumerateScheduledTasksSafe())
        {
            var text = $"{task.TaskPath}{task.TaskName} {task.ExecutePath}";
            if (!text.Contains("Lenovo", StringComparison.OrdinalIgnoreCase) &&
                !text.Contains("Legion", StringComparison.OrdinalIgnoreCase) &&
                !text.Contains("PCManager", StringComparison.OrdinalIgnoreCase) &&
                !text.Contains("SystemIdleMonitor", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var exists = !string.IsNullOrWhiteSpace(task.ExecutePath) && File.Exists(Environment.ExpandEnvironmentVariables(task.ExecutePath));
            findings.Add(LeftoverClassifier.ClassifyScheduledTask(
                task.TaskName,
                task.TaskPath,
                task.ExecutePath,
                exists,
                task.StateLabel,
                isPcManagerInstalled,
                isLegionZoneInstalled));
        }

        // 2. Current-user AppX packages related to Lenovo / Legion / PC Manager.
        foreach (var app in installed.Where(a => a.Scope == InstallScope.StoreAppx))
        {
            var identityText = $"{app.DisplayName} {app.StableId}";
            if (!identityText.Contains("Lenovo", StringComparison.OrdinalIgnoreCase) &&
                !identityText.Contains("Legion", StringComparison.OrdinalIgnoreCase) &&
                !identityText.Contains("A0Pcm", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            findings.Add(LeftoverClassifier.ClassifyAppxPackage(
                app.DisplayName,
                app.StableId.Replace("StoreAppx:", string.Empty, StringComparison.Ordinal),
                app.Version,
                isPcManagerInstalled,
                isLegionZoneInstalled));
        }

        // 3. Lenovo / Legion Windows services (read-only status via Win32_Service).
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\cimv2",
                "SELECT Name, DisplayName, State, StartMode FROM Win32_Service");
            foreach (ManagementObject svc in searcher.Get())
            {
                var svcName = svc["Name"]?.ToString() ?? string.Empty;
                var displayName = svc["DisplayName"]?.ToString() ?? svcName;
                if (!svcName.Contains("Lenovo", StringComparison.OrdinalIgnoreCase) &&
                    !svcName.Contains("LZService", StringComparison.OrdinalIgnoreCase) &&
                    !svcName.Contains("GAService", StringComparison.OrdinalIgnoreCase) &&
                    !svcName.Contains("ImController", StringComparison.OrdinalIgnoreCase) &&
                    !displayName.Contains("Lenovo", StringComparison.OrdinalIgnoreCase) &&
                    !displayName.Contains("Legion", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                findings.Add(LeftoverClassifier.ClassifyService(
                    svcName,
                    displayName,
                    svc["State"]?.ToString() ?? "Unknown",
                    svc["StartMode"]?.ToString() ?? "Unknown",
                    isPcManagerInstalled,
                    isLegionZoneInstalled));
            }
        }
        catch
        {
            // Ignore service enumeration errors if restricted.
        }

        return findings
            .OrderBy(f => f.Classification)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private sealed record TaskInfo(
        string TaskName,
        string TaskPath,
        bool IsEnabled,
        string StateLabel,
        bool HasLogonOrBootTrigger,
        string? ExecutePath);

    private static List<TaskInfo> EnumerateScheduledTasksSafe()
    {
        var list = new List<TaskInfo>();
        object? scheduler = null;
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null) return list;

            scheduler = Activator.CreateInstance(type);
            if (scheduler is null) return list;

            dynamic dynScheduler = scheduler;
            dynScheduler.Connect();
            var rootFolder = dynScheduler.GetFolder("\\");
            WalkTaskFolder(rootFolder, "\\", list);
        }
        catch
        {
            // Return whatever was collected.
        }
        finally
        {
            if (scheduler is not null && Marshal.IsComObject(scheduler))
            {
                Marshal.FinalReleaseComObject(scheduler);
            }
        }

        return list;
    }

    private static void WalkTaskFolder(dynamic folder, string pathPrefix, List<TaskInfo> output)
    {
        dynamic tasks = folder.GetTasks(1); // TASK_ENUM_HIDDEN
        foreach (dynamic task in tasks)
        {
            try
            {
                string name = task.Name;
                bool enabled = task.Enabled;
                int state = task.State;
                string stateLabel = state switch
                {
                    1 => "Disabled",
                    2 => "Queued",
                    3 => "Ready",
                    4 => "Running",
                    _ => "Unknown"
                };

                bool hasLogonOrBoot = false;
                string? execPath = null;
                dynamic def = task.Definition;
                foreach (dynamic trig in def.Triggers)
                {
                    int trigType = trig.Type;
                    if (trigType is 8 or 9) // BOOT=8, LOGON=9
                    {
                        hasLogonOrBoot = true;
                        break;
                    }
                }

                foreach (dynamic action in def.Actions)
                {
                    if ((int)action.Type == 0) // TASK_ACTION_EXEC
                    {
                        execPath = action.Path?.ToString();
                        break;
                    }
                }

                output.Add(new TaskInfo(name, pathPrefix, enabled, stateLabel, hasLogonOrBoot, execPath));
            }
            catch
            {
                // Skip unreadable individual task entries.
            }
        }

        dynamic subFolders = folder.GetFolders(0);
        foreach (dynamic sub in subFolders)
        {
            string subPath = sub.Path;
            if (!subPath.EndsWith("\\", StringComparison.Ordinal))
            {
                subPath += "\\";
            }

            WalkTaskFolder(sub, subPath, output);
        }
    }

    private static bool TryLaunchUri(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
