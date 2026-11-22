using System.Xml.Linq;
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
    public Task<IReadOnlyList<StartupEntry>> ReadStartupEntriesAsync(CancellationToken ct = default) =>
        Task.Run(() => ReadStartupEntries(EnumerateScheduledTasksSafe(true, ct)), ct);

    public Task<SoftwareInventorySnapshot> ScanAsync(bool includeLenovoLeftovers, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var now = DateTimeOffset.UtcNow;
            var installed = ReadInstalledApps();
            var tasks = EnumerateScheduledTasksSafe(!includeLenovoLeftovers, ct);
            var startup = ReadStartupEntries(tasks);
            var leftovers = includeLenovoLeftovers ? ScanLenovoArtifactsReadOnly(installed, tasks) : [];
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

    public async Task<OperationResult<bool>> SetStartupEntryEnabledAsync(StartupEntry entry, bool enabled, CancellationToken ct = default)
    {
        var direct = await Task.Run(() => SetStartupEntryEnabledDirect(entry.StableId, enabled), ct).ConfigureAwait(false);
        if (direct.ErrorCode != "ACCESS_DENIED")
            return direct;

        // System-wide entry (HKLM / all-users folder / protected task): same change through the elevated worker.
        var response = await new Lenovo.ElevatedWorkerClient().ExecuteRequestAsync(new ElevatedRequest
        {
            AuthToken = string.Empty,
            Operation = ElevatedOperation.SetStartupEntryEnabled,
            StartupEntryId = entry.StableId,
            StartupEnabled = enabled
        }, ct).ConfigureAwait(false);
        if (response.WasCancelled)
            return OperationResult<bool>.Cancelled();
        return response.Success
            ? OperationResult<bool>.Ok(enabled)
            : OperationResult<bool>.Fail(response.ErrorCode ?? "STARTUP_TOGGLE_FAILED", response.Message ?? string.Empty);
    }

    private const string ApprovedRoot = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    /// <summary>
    /// Toggles one startup entry, looked up again by StableId (so only an entry that really exists is touched).
    /// Run keys and startup folders use the StartupApproved flags that Task Manager / Settings use; logon tasks
    /// are enabled or disabled in Task Scheduler. Returns ACCESS_DENIED when elevation is needed.
    /// </summary>
    public static OperationResult<bool> SetStartupEntryEnabledDirect(string? stableId, bool enabled)
    {
        var tasks = stableId?.StartsWith("Task:", StringComparison.Ordinal) == true
            ? EnumerateScheduledTasksSafe(true, CancellationToken.None) : [];
        var entry = ReadStartupEntries(tasks).FirstOrDefault(e => e.StableId == stableId);
        if (entry is null)
            return OperationResult<bool>.Fail("STARTUP_ENTRY_NOT_FOUND", "Startup entry no longer exists.");

        try
        {
            switch (entry.Source)
            {
                case StartupEntrySource.UserRunRegistry:
                    WriteApprovedFlag(RegistryHive.CurrentUser, "Run", entry.Name, enabled);
                    break;
                case StartupEntrySource.MachineRunRegistry:
                    WriteApprovedFlag(RegistryHive.LocalMachine, entry.Location.StartsWith("HKLM(x86)", StringComparison.Ordinal) ? "Run32" : "Run",
                        entry.Name, enabled);
                    break;
                case StartupEntrySource.UserStartupFolder:
                    WriteApprovedFlag(RegistryHive.CurrentUser, "StartupFolder", Path.GetFileName(entry.CommandOrTarget), enabled);
                    break;
                case StartupEntrySource.MachineStartupFolder:
                    WriteApprovedFlag(RegistryHive.LocalMachine, "StartupFolder", Path.GetFileName(entry.CommandOrTarget), enabled);
                    break;
                case StartupEntrySource.ScheduledTask:
                    SetTaskEnabled(entry.Location, entry.Name, enabled);
                    break;
            }
            return OperationResult<bool>.Ok(enabled);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException ||
                                   (ex is COMException com && com.HResult == unchecked((int)0x80070005)))
        {
            return OperationResult<bool>.Fail("ACCESS_DENIED", ex.Message);
        }
        catch (Exception ex)
        {
            return OperationResult<bool>.Fail("STARTUP_TOGGLE_FAILED", ex.Message);
        }
    }

    /// <summary>
    /// StartupApproved value: 12 bytes, low bit of byte 0 set = disabled; bytes 4..11 hold the FILETIME it was
    /// disabled (zero when enabled). Other bits of an existing value are preserved.
    /// </summary>
    private static void WriteApprovedFlag(RegistryHive hive, string keyName, string valueName, bool enabled)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = baseKey.CreateSubKey(ApprovedRoot + keyName, writable: true);
        var data = new byte[12];
        if (key.GetValue(valueName) is byte[] existing && existing.Length >= 1)
            Array.Copy(existing, data, Math.Min(existing.Length, data.Length));
        else
            data[0] = 0x02;

        if (enabled)
        {
            data[0] = (byte)(data[0] & ~0x01);
            Array.Clear(data, 4, 8);
        }
        else
        {
            data[0] = (byte)(data[0] | 0x01);
            BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
        }
        key.SetValue(valueName, data, RegistryValueKind.Binary);
    }

    private static void SetTaskEnabled(string folderPath, string taskName, bool enabled)
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Task Scheduler unavailable.");
        var scheduler = Activator.CreateInstance(type) ?? throw new InvalidOperationException("Task Scheduler unavailable.");
        try
        {
            dynamic dynScheduler = scheduler;
            dynScheduler.Connect();
            var path = folderPath.Length > 1 ? folderPath.TrimEnd('\\') : "\\";
            dynamic task = dynScheduler.GetFolder(path).GetTask(taskName);
            task.Enabled = enabled;
        }
        finally
        {
            if (Marshal.IsComObject(scheduler))
                Marshal.FinalReleaseComObject(scheduler);
        }
    }

    internal static IReadOnlyList<InstalledAppEntry> ReadInstalledApps(bool includeStoreApps = true)
    {
        var map = new Dictionary<string, InstalledAppEntry>(StringComparer.OrdinalIgnoreCase);

        ReadUninstallHive(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", InstallScope.MachineX64, map);
        ReadUninstallHive(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", InstallScope.MachineX86, map);
        ReadUninstallHive(RegistryHive.CurrentUser, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", InstallScope.CurrentUser, map);
        if (includeStoreApps)
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

            var app = ReadDesktopRegistration(sub, scope, childName);
            if (app is not null) map.TryAdd(app.StableId, app);
        }
    }

    internal static InstalledAppEntry? ReadDesktopRegistration(RegistryKey sub, InstallScope scope, string childName)
    {
        var systemComponent = sub.GetValue("SystemComponent");
        if (systemComponent is int sc && sc == 1) return null;

        var releaseType = sub.GetValue("ReleaseType")?.ToString();
        if (!string.IsNullOrWhiteSpace(releaseType)) return null;

        var name = sub.GetValue("DisplayName")?.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return null;

        var publisher = sub.GetValue("Publisher")?.ToString()?.Trim() ?? string.Empty;
        var version = sub.GetValue("DisplayVersion")?.ToString()?.Trim() ?? string.Empty;
        var stableId = $"{scope}:{childName}";

        return new InstalledAppEntry(
            StableId: stableId,
            DisplayName: name,
            Publisher: publisher,
            Version: version,
            Scope: scope,
            PackageFamilyName: null,
            SupportsWindowsAppsDeepLink: false)
        {
            DesktopRegistration = new DesktopUninstallRegistration(
                childName, sub.GetValue("UninstallString") as string, sub.GetValue("InstallLocation") as string,
                sub.GetValue("WindowsInstaller") is 1, sub.GetValue("NoRemove") is 1)
        };
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

    private static IReadOnlyList<StartupEntry> ReadStartupEntries(List<TaskInfo> tasks)
    {
        var entries = new List<StartupEntry>();

        ReadRunKey(RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\Run",
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", StartupEntrySource.UserRunRegistry, "HKCU\\...\\Run", entries);
        ReadRunKey(RegistryHive.LocalMachine, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\Run",
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", StartupEntrySource.MachineRunRegistry, "HKLM\\...\\Run", entries);
        ReadRunKey(RegistryHive.LocalMachine, RegistryView.Registry32, @"Software\Microsoft\Windows\CurrentVersion\Run",
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32", StartupEntrySource.MachineRunRegistry, "HKLM(x86)\\...\\Run", entries);

        ReadStartupFolder(RegistryHive.CurrentUser, Environment.GetFolderPath(Environment.SpecialFolder.Startup), StartupEntrySource.UserStartupFolder, entries);
        ReadStartupFolder(RegistryHive.LocalMachine, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), StartupEntrySource.MachineStartupFolder, entries);
        ReadLogonScheduledTasks(entries, tasks);

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

        // StartupApproved (incl. Run32) lives in the 64-bit view even for the 32-bit Run key.
        using var approvedBase = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var approvedKey = approvedBase.OpenSubKey(approvedPath);
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

    private static void ReadStartupFolder(RegistryHive hive, string folderPath, StartupEntrySource source, List<StartupEntry> target)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return;
        }

        using var approvedBase = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var approvedKey = approvedBase.OpenSubKey(ApprovedRoot + "StartupFolder");
        foreach (var file in Directory.EnumerateFiles(folderPath))
        {
            var name = Path.GetFileName(file);
            if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            var enabled = approvedKey?.GetValue(name) is not byte[] { Length: >= 1 } bytes || (bytes[0] & 0x01) == 0;
            target.Add(new StartupEntry(
                StableId: $"{source}:{file}",
                Name: Path.GetFileNameWithoutExtension(file),
                CommandOrTarget: file,
                Source: source,
                IsEnabled: enabled,
                Location: folderPath));
        }
    }

    private static void ReadLogonScheduledTasks(List<StartupEntry> target, List<TaskInfo> tasks)
    {
        foreach (var task in tasks)
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

    private static IReadOnlyList<LenovoLeftoverFinding> ScanLenovoArtifactsReadOnly(IReadOnlyList<InstalledAppEntry> installed, List<TaskInfo> tasks)
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
        foreach (var task in tasks)
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

    private static List<TaskInfo> EnumerateScheduledTasksSafe(bool startupOnly, CancellationToken ct)
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
            WalkTaskFolder(rootFolder, "\\", list, startupOnly, ct);
        }
        catch (COMException)
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

    private static readonly XNamespace TaskSchema = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private static void WalkTaskFolder(dynamic folder, string pathPrefix, List<TaskInfo> output,
        bool startupOnly, CancellationToken ct)
    {
        try
        {
            foreach (dynamic task in folder.GetTasks(1)) // Include hidden tasks.
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    // Reading the definition once avoids a COM round-trip for every trigger and action.
                    var definition = XElement.Parse((string)task.Xml);
                    var triggers = definition.Element(TaskSchema + "Triggers");
                    bool startup = triggers?.Elements().Any(t => t.Name == TaskSchema + "BootTrigger" ||
                        t.Name == TaskSchema + "LogonTrigger") == true;
                    if (startupOnly && !startup) continue;
                    var exe = (string?)definition.Element(TaskSchema + "Actions")?
                        .Element(TaskSchema + "Exec")?.Element(TaskSchema + "Command");
                    int state = startupOnly ? 0 : (int)task.State;
                    string stateLabel = state switch { 1 => "Disabled", 2 => "Queued", 3 => "Ready", 4 => "Running", _ => "Unknown" };
                    output.Add(new TaskInfo((string)task.Name, pathPrefix, (bool)task.Enabled, stateLabel, startup, exe));
                }
                catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or System.Xml.XmlException)
                {
                    // The task may have been changed, removed or restricted during enumeration.
                }
                finally { Marshal.ReleaseComObject(task); }
            }

            foreach (dynamic child in folder.GetFolders(0))
            {
                ct.ThrowIfCancellationRequested();
                string path = child.Path;
                WalkTaskFolder(child, path.TrimEnd('\\') + "\\", output, startupOnly, ct);
            }
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            // A restricted folder does not prevent scanning its siblings.
        }
        finally { Marshal.ReleaseComObject(folder); }
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
