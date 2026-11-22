using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Utility.Platform.Providers;

public sealed record RegistryResidual(RegistryHive Hive, RegistryView View, string Key, string? Value, string Fingerprint)
{
    public string FullKey => $"{(Hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM")}\\{Key}";
    public string DisplayPath => $"{FullKey} [{(View == RegistryView.Registry32 ? "32" : "64")}]"
        + (Value is null ? string.Empty : $" :: {Value}");
}

public sealed record AppResidual(ScannedFile? File, RegistryResidual? Registry, string Evidence)
{
    public string Path => File?.Path ?? Registry!.DisplayPath;
}

public sealed record ResidualScanResult(IReadOnlyList<AppResidual> Items, bool Limited);

public sealed class UninstallResidualService(WindowsUninstallService uninstall)
{
    private const int MaxFiles = 3000;
    private const int MaxRegistryNodes = 512;
    private static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string Roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static readonly string Common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    private static readonly string[] ProgramRoots =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Path.Combine(Local, "Programs")
    ];
    private static readonly string[] AppDataRoots = [Local, Roaming, Common];
    private static readonly string LastUninstallPath = Path.Combine(Local, "Utility", "LastUninstall.json");
    private static readonly string[] SharedNames =
    [
        "windows", "microsoft", "lenovo", "system", "programs", "packages",
        "common", "commonfiles", "resources", "downloads", "utilities", "utility"
    ];

    public UninstallTarget? LoadLastUninstall()
    {
        try
        {
            var info = new FileInfo(LastUninstallPath);
            if (!info.Exists || info.Length > 65536 || !DiskCleanupService.HasSafeParents(info.FullName))
                return null;
            return JsonSerializer.Deserialize<UninstallTarget>(File.ReadAllText(info.FullName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void SaveLastUninstall(UninstallTarget target)
    {
        // The receipt is best effort. The active session can still scan a completed uninstall.
        try
        {
            var parent = Path.GetDirectoryName(LastUninstallPath)!;
            Directory.CreateDirectory(parent);
            if (!DiskCleanupService.HasSafeParents(parent))
                return;
            var temp = Path.Combine(parent, $"LastUninstall-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temp, JsonSerializer.Serialize(target));
            File.Move(temp, LastUninstallPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public async Task<ResidualScanResult> ScanAsync(UninstallTarget target, CancellationToken ct)
    {
        if (!await uninstall.IsUninstalledAsync(target, ct).ConfigureAwait(false))
            throw new InvalidOperationException("Windows still lists this app. Finish uninstalling it before scanning leftovers.");

        return await Task.Run(() => Scan(target, ct), ct).ConfigureAwait(false);
    }

    public async Task<CleanupBatchResult> RemoveAsync(UninstallTarget target, IReadOnlyList<AppResidual> selected)
    {
        if (!await uninstall.IsUninstalledAsync(target, CancellationToken.None).ConfigureAwait(false))
            throw new InvalidOperationException("The app is installed again. Scan after uninstalling it.");

        return await Task.Run(() => Remove(target, selected)).ConfigureAwait(false);
    }

    private static ResidualScanResult Scan(UninstallTarget target, CancellationToken ct)
    {
        var footprints = GetFootprints(target);
        var items = new List<AppResidual>();
        var locations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var limited = false;
        foreach (var folder in footprints.Folders)
        {
            foreach (var file in DiskCleanupService.WalkFiles(folder.Path, _ => false, ct))
            {
                if (items.Count >= MaxFiles)
                {
                    limited = true;
                    break;
                }
                if (locations.Add(file.Path))
                    items.Add(new AppResidual(file, null, folder.Evidence));
            }
            if (limited)
                break;
        }

        foreach (var (hive, view) in new[]
                 {
                     (RegistryHive.CurrentUser, RegistryView.Registry64),
                     (RegistryHive.LocalMachine, RegistryView.Registry64),
                     (RegistryHive.LocalMachine, RegistryView.Registry32)
                 })
        {
            ct.ThrowIfCancellationRequested();
            if (target.PackageName is not null && hive != RegistryHive.CurrentUser)
                continue;

            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            foreach (var name in footprints.ProductNames)
            {
                AddKey(@"Software\" + name, "ExactProductRegistry");
                if (footprints.Publisher is { } publisher)
                    AddKey($@"Software\{publisher}\{name}", "ExactProductRegistry");
            }
            if (footprints.InstallPath is { } installPath)
            {
                ScanValueReferences(@"Software\Microsoft\Windows\CurrentVersion\Run");
                ScanValueReferences(@"Software\Microsoft\Windows\CurrentVersion\RunOnce");
                ScanCommandChildren(@"Software\Microsoft\Windows\CurrentVersion\App Paths", child => child, "");
                ScanCommandChildren(@"Software\Classes\Applications", child => $@"{child}\shell\open\command", "");
            }

            void AddKey(string path, string reason)
            {
                var fingerprint = Fingerprint(baseKey, path, null, ct);
                if (fingerprint is null)
                    return;
                var residual = new RegistryResidual(hive, view, path, null, fingerprint);
                if (locations.Add(residual.DisplayPath))
                    items.Add(new AppResidual(null, residual, reason));
            }

            void AddReference(string path, string value)
            {
                var fingerprint = Fingerprint(baseKey, path, value, ct);
                if (fingerprint is null) return;
                var residual = new RegistryResidual(hive, view, path, value, fingerprint);
                if (locations.Add(residual.DisplayPath))
                    items.Add(new AppResidual(null, residual, "InstallReferenceRegistry"));
            }

            void ScanValueReferences(string path)
            {
                using var key = TryOpen(baseKey, path);
                if (key is null)
                    return;
                foreach (var value in key.GetValueNames())
                {
                    ct.ThrowIfCancellationRequested();
                    if (ReferencesInstallation(key.GetValue(value, null, RegistryValueOptions.DoNotExpandEnvironmentNames),
                            footprints.InstallPath!) && value.Length > 0)
                    {
                        AddReference(path, value);
                    }
                }
            }

            void ScanCommandChildren(string path, Func<string, string> getCommandPath, string value)
            {
                using var parent = TryOpen(baseKey, path);
                if (parent is null)
                    return;
                foreach (var child in parent.GetSubKeyNames())
                {
                    ct.ThrowIfCancellationRequested();
                    using var command = TryOpen(parent, getCommandPath(child));
                    if (command is not null &&
                        ReferencesInstallation(command.GetValue(value, null, RegistryValueOptions.DoNotExpandEnvironmentNames),
                            footprints.InstallPath!))
                    {
                        // The default command belongs to the app. Other verbs and values can belong to another app.
                        AddReference(path + "\\" + getCommandPath(child), value);
                    }
                }
            }
        }
        return new ResidualScanResult(items, limited);
    }

    private static CleanupBatchResult Remove(UninstallTarget target, IReadOnlyList<AppResidual> selected)
    {
        // Recompute paths from the original app identity and compare hashes before writing any registry changes.
        var current = Scan(target, CancellationToken.None).Items.ToHashSet();
        var removed = new List<string>();
        var errors = new List<string>();
        string? backup = null;
        var backups = new Dictionary<(RegistryHive, RegistryView, string), string>();
        foreach (var item in selected.Distinct())
        {
            try
            {
                if (!current.Contains(item))
                    throw new IOException("The item changed since the scan or can no longer be associated with the app.");
                if (item.File is { } file)
                {
                    if (!DiskCleanupService.Matches(file))
                        throw new IOException("File changed since the scan.");
                    DiskCleanupService.Recycle(file.Path);
                }
                else if (item.Registry is { } registry)
                {
                    using var root = RegistryKey.OpenBaseKey(registry.Hive, registry.View);
                    if (Fingerprint(root, registry.Key, registry.Value, CancellationToken.None) != registry.Fingerprint)
                        throw new IOException("Registry contents changed since the scan.");

                    var parentPath = registry.Value is null ? registry.Key[..registry.Key.LastIndexOf('\\')] : registry.Key;
                    using var writable = root.OpenSubKey(parentPath, writable: true)
                        ?? throw new IOException("Registry parent could not be opened for writing.");

                    backup ??= CreateBackupFolder();
                    var recovery = (registry.Hive, registry.View, registry.Key);
                    if (!backups.ContainsKey(recovery))
                    {
                        var filePath = Path.Combine(backup, $"{backups.Count + 1:D4}-{(registry.View == RegistryView.Registry32 ? 32 : 64)}.reg");
                        ExportKey(registry, filePath);
                        backups.Add(recovery, filePath);
                    }

                    if (Fingerprint(root, registry.Key, registry.Value, CancellationToken.None) != registry.Fingerprint)
                        throw new IOException("Registry contents changed during backup.");
                    if (registry.Value is { } value)
                        writable.DeleteValue(value, throwOnMissingValue: true);
                    else
                        writable.DeleteSubKeyTree(registry.Key[(registry.Key.LastIndexOf('\\') + 1)..], throwOnMissingSubKey: true);
                }
                removed.Add(item.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException
                                       or System.Security.SecurityException or System.ComponentModel.Win32Exception)
            {
                errors.Add($"{item.Path} — {ex.Message}");
            }
        }
        return new CleanupBatchResult(removed, errors, backups.Count > 0 ? backup : null);
    }

    private static RegistryKey? TryOpen(RegistryKey root, string path)
    {
        try { return root.OpenSubKey(path); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return null; }
    }

    private static string? Fingerprint(RegistryKey root, string path, string? value, CancellationToken ct)
    {
        try
        {
            using var key = TryOpen(root, path);
            if (key is null)
                return null;

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var count = 0;
            var length = 0L;
            if (value is not null)
            {
                if (!key.GetValueNames().Contains(value, StringComparer.OrdinalIgnoreCase))
                    return null;
                AddValue(key, value);
            }
            else if (!AddTree(key, 0))
            {
                return null;
            }
            return Convert.ToHexString(hash.GetHashAndReset());

            bool AddTree(RegistryKey node, int depth)
            {
                ct.ThrowIfCancellationRequested();
                if (++count > MaxRegistryNodes || depth > 12)
                    return false;
                Append(node.Name);
                var names = node.GetValueNames();
                if (names.Contains("SymbolicLinkValue", StringComparer.OrdinalIgnoreCase))
                    return false;
                foreach (var name in names.Order(StringComparer.OrdinalIgnoreCase))
                {
                    AddValue(node, name);
                    if (length > 4 * 1024 * 1024)
                        return false;
                }
                foreach (var name in node.GetSubKeyNames().Order(StringComparer.OrdinalIgnoreCase))
                {
                    using var child = TryOpen(node, name);
                    if (child is null || !AddTree(child, depth + 1))
                        return false;
                }
                return true;
            }

            void AddValue(RegistryKey node, string name)
            {
                Append(name);
                Append(node.GetValueKind(name).ToString());
                Append(JsonSerializer.Serialize(node.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)));
            }

            void Append(string text)
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                length += bytes.Length;
                hash.AppendData(BitConverter.GetBytes(bytes.Length));
                hash.AppendData(bytes);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return null;
        }
    }

    private static (IReadOnlyList<(string Path, string Evidence)> Folders, string[] ProductNames,
        string? Publisher, string? InstallPath) GetFootprints(UninstallTarget target)
    {
        var folders = new List<(string Path, string Evidence)>();
        if (target.PackageName is not null && SafeSegment(target.App.PackageFamilyName) is { } family)
        {
            folders.Add((Path.Combine(Local, "Packages", family), "PackageData"));
            return (folders, new[] { family, target.PackageName }.Where(name => SafeSegment(name) is not null).ToArray(), null, null);
        }

        var apps = WindowsSoftwareProvider.ReadInstalledApps(includeStoreApps: false);
        var otherNames = apps.SelectMany(app => new[] { Normalize(app.DisplayName), Normalize(app.Publisher) })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var publisher = SafeSegment(target.App.Publisher);
        var product = SafeSegment(target.App.DisplayName);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (product is not null && !otherNames.Contains(Normalize(product)))
            names.Add(product);

        string? installPath = null;
        var location = NormalizePath(target.App.DesktopRegistration?.InstallLocation);
        if (location is not null && ProgramRoots.Any(root => DiskCleanupService.IsChild(location, root)) &&
            SafeSegment(Path.GetFileName(location)) is { } installName &&
            ((product is not null && Normalize(product) == Normalize(installName)) ||
             RegisteredExeIsInside(target.App.DesktopRegistration?.Command, location)) &&
            !apps.Any(app => Overlaps(location, NormalizePath(app.DesktopRegistration?.InstallLocation)) ||
                RegisteredExeIsInside(app.DesktopRegistration?.Command, location)))
        {
            installPath = location;
            folders.Add((location, "RegisteredInstallationFolder"));
            if (!otherNames.Contains(Normalize(installName)))
                names.Add(installName);
        }

        foreach (var name in names)
        {
            foreach (var root in AppDataRoots)
            {
                folders.Add((Path.Combine(root, name), "ExactProductFolder"));
                if (publisher is not null)
                    folders.Add((Path.Combine(root, publisher, name), "ExactProductFolder"));
            }
        }
        return (folders, names.ToArray(), publisher, installPath);
    }

    private static string Normalize(string name) => string.Concat(name.Where(char.IsLetterOrDigit)).ToUpperInvariant();

    private static string? SafeSegment(string? value)
    {
        var name = value?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.EndsWith('.') || name.Length > 120 ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Normalize(name).Length < 4 ||
            SharedNames.Contains(Normalize(name), StringComparer.OrdinalIgnoreCase))
            return null;
        return name;
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            return Path.IsPathFullyQualified(expanded) && DiskCleanupService.IsFixedDrive(expanded)
                ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded)) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool Overlaps(string path, string? other) =>
        other is not null && (DiskCleanupService.IsInOrEqual(path, other) || DiskCleanupService.IsInOrEqual(other, path));

    private static bool RegisteredExeIsInside(string? command, string folder) =>
        command is not null && WindowsUninstallService.SplitExecutable(Environment.ExpandEnvironmentVariables(command)) is { } executable
        && NormalizePath(executable.Path) is { } path && DiskCleanupService.IsChild(path, folder);

    private static bool ReferencesInstallation(object? value, string folder) =>
        value is string text && RegisteredExeIsInside(text, folder);

    private static string CreateBackupFolder()
    {
        var path = Path.Combine(Local, "Utility", "CleanupBackups", $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        if (!DiskCleanupService.HasSafeParents(path))
            throw new IOException("Registry backup folder is redirected.");
        return path;
    }

    private static void ExportKey(RegistryResidual registry, string file)
    {
        var system = registry.View == RegistryView.Registry32 && Environment.Is64BitOperatingSystem
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64")
            : Environment.SystemDirectory;
        var start = new ProcessStartInfo(Path.Combine(system, "reg.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "export", registry.FullKey, file })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Windows registry export could not start.");
        process.WaitForExit();
        if (process.ExitCode != 0 || !File.Exists(file) || new FileInfo(file).Length == 0)
            throw new IOException("Registry backup failed. This item was retained.");
    }
}
