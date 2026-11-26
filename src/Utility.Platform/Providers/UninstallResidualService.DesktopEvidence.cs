using System.Security.Cryptography;
using Microsoft.Win32;

namespace Utility.Platform.Providers;

public sealed partial class UninstallResidualService
{
    private static DesktopUninstallEvidence? CaptureDesktopEvidence(
        UninstallTarget target, string installPath, string[] productNames, CancellationToken ct)
    {
        if (ReadDesktopRegistrationResidual(target, ct) is not { } registration)
            return null;

        var installer = GetCommandExecutable(target.App.DesktopRegistration!.Command);
        var launchers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var icon = target.App.DesktopRegistration.DisplayIcon;
        var separator = icon?.LastIndexOf(',') ?? -1;
        if (separator >= 0 && int.TryParse(icon.AsSpan(separator + 1), out _))
            icon = icon![..separator];
        AddLauncher(NormalizePath(icon));

        foreach (var item in target.ResidualClasses ?? [])
        {
            using var root = RegistryKey.OpenBaseKey(item.Hive, item.View);
            using var command = TryOpen(root, $@"{ClassesPath}\{item.Name}\{OpenCommandPath}");
            if (command is not null)
                AddLauncher(GetCommandExecutable(ReadValue(command, "") as string));
        }
        foreach (var (_, destination) in ScanShortcuts(installPath, productNames, ct))
            AddLauncher(destination);

        return launchers.Count is > 0 and <= 64
            ? new DesktopUninstallEvidence(registration.Fingerprint, launchers.ToArray())
            : null;

        void AddLauncher(string? path)
        {
            if (path is not null && DiskCleanupService.IsChild(path, installPath) &&
                !path.Equals(installer, StringComparison.OrdinalIgnoreCase) &&
                Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(path) && DiskCleanupService.HasSafeParents(path))
                launchers.Add(path);
        }
    }

    private (ResidualCleanupStatus Status, RegistryResidual? Registration) GetCleanupStatus(
        UninstallTarget target, string? installPath, CancellationToken ct)
    {
        if (uninstall.IsUninstalled(target, ct))
            return (ResidualCleanupStatus.Ready, null);

        if (target.PackageName is not null || target.DesktopExitCode != 0 || installPath is null ||
            target.App.DesktopRegistration is not { IsMsi: false } desktop ||
            target.DesktopEvidence is not { Launchers.Length: > 0 and <= 64 } evidence ||
            ReadDesktopRegistrationResidual(target, ct) is not { } registration ||
            registration.Fingerprint != evidence.RegistrationFingerprint)
            return (ResidualCleanupStatus.ReviewOnly, null);

        var installer = GetCommandExecutable(desktop.Command);
        // A working launcher or any surviving executable other than the uninstaller keeps cleanup blocked.
        // Some installers leave byte-for-byte copies of their bootstrapper under another name.
        if (string.Equals(Path.GetFileName(installer), "msiexec.exe", StringComparison.OrdinalIgnoreCase) ||
            !evidence.Launchers.All(path => NormalizePath(path) == path &&
                DiskCleanupService.IsChild(path, installPath) && IsMissing(path)) ||
            !OnlyInstallerExecutablesRemain(installPath, installer, ct))
            return (ResidualCleanupStatus.ReviewOnly, null);

        return (ResidualCleanupStatus.StaleRegistration, registration);
    }

    private static RegistryResidual? ReadDesktopRegistrationResidual(UninstallTarget target, CancellationToken ct)
    {
        var name = target.App.DesktopRegistration?.RegistryKeyName;
        if (string.IsNullOrEmpty(name) || name.Contains('\\'))
            return null;

        var (hive, view) = WindowsUninstallService.UninstallRegistryLocation(target.App.Scope);
        var path = $@"{WindowsUninstallService.UninstallKey}\{name}";
        using var root = RegistryKey.OpenBaseKey(hive, view);
        using var key = TryOpen(root, path);
        return key is not null && WindowsSoftwareProvider.ReadDesktopRegistration(key, target.App.Scope, name) == target.App &&
            Fingerprint(root, path, null, ct) is { } fingerprint
                ? new RegistryResidual(hive, view, path, null, fingerprint)
                : null;
    }

    private static string? GetCommandExecutable(string? command) => command is not null &&
        WindowsUninstallService.SplitExecutable(Environment.ExpandEnvironmentVariables(command)) is { } split
            ? NormalizePath(split.Path)
            : null;

    private static bool IsMissing(string path)
    {
        try
        {
            File.GetAttributes(path);
            return false;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool OnlyInstallerExecutablesRemain(string root, string? installer, CancellationToken ct)
    {
        if (IsMissing(root))
            return true;

        try
        {
            if (!DiskCleanupService.HasSafeParents(root))
                return false;

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = false,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Offline
            };
            var seen = 0;
            var comparedBytes = 0L;
            var installerSize = -1L;
            byte[]? installerHash = null;
            foreach (var executable in Directory.EnumerateFileSystemEntries(root, "*", options))
            {
                ct.ThrowIfCancellationRequested();
                if (++seen > 10_000)
                    return false;
                if (!Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                    executable.Equals(installer, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (installerHash is null)
                {
                    if (installer is null || !DiskCleanupService.IsChild(installer, root) ||
                        !DiskCleanupService.HasSafeParents(installer))
                        return false;
                    installerSize = new FileInfo(installer).Length;
                    if (installerSize > 64L * 1024 * 1024)
                        return false;
                    installerHash = HashFile(installer);
                }
                comparedBytes += installerSize;
                if (comparedBytes > 128L * 1024 * 1024 || new FileInfo(executable).Length != installerSize ||
                    !HashFile(executable).AsSpan().SequenceEqual(installerHash))
                    return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static byte[] HashFile(string path)
    {
        using var file = File.OpenRead(path);
        return SHA256.HashData(file);
    }
}
