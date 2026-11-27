using Microsoft.Win32;
using Utility.Core.Models;

namespace Utility.Platform.Providers;

public sealed partial class UninstallResidualService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovedRunKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ApkDisplayName = "APK";
    private const string ApkDataName = "apkstart";
    private const string ApkInstallerId = "c99f334e-0eed-51c5-9914-8835e31e2d12";

    private static string? GetDesktopInstallLocation(UninstallTarget target, bool ownsApkStartIdentity)
    {
        var registration = target.App.DesktopRegistration;
        if (NormalizePath(registration?.InstallLocation) is { } location)
            return location;

        if (GetCommandExecutable(registration?.Command) is not { } uninstaller ||
            NormalizePath(Path.GetDirectoryName(uninstaller)) is not { } directory)
            return null;

        if (ownsApkStartIdentity ||
            Path.GetFileName(uninstaller).StartsWith("unins", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(NormalizePath(target.CapturedInstallLocation), directory, StringComparison.OrdinalIgnoreCase) ||
            HasMatchingInstallerLocation(target.App, directory))
            return directory;

        return null;
    }

    private static bool HasMatchingInstallerLocation(InstalledAppEntry app, string directory)
    {
        if (app.DesktopRegistration is not { IsMsi: false, RegistryKeyName: var id } ||
            !(Guid.TryParseExact(id, "D", out _) || Guid.TryParseExact(id, "B", out _)) ||
            app.Scope is not (InstallScope.CurrentUser or InstallScope.MachineX64 or InstallScope.MachineX86))
            return false;

        var (hive, view) = WindowsUninstallService.UninstallRegistryLocation(app.Scope);
        using var root = RegistryKey.OpenBaseKey(hive, view);
        using var installer = TryOpen(root, @"Software\" + id);
        // An installer key is evidence only when its recorded location agrees with the registered uninstall command.
        return string.Equals(NormalizePath(installer?.GetValue("InstallLocation") as string), directory,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsApkStart(InstalledAppEntry app)
    {
        if (app.Scope != InstallScope.CurrentUser ||
            !string.Equals(app.DisplayName, ApkDisplayName, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(app.Publisher, "RoutineMiku", StringComparison.OrdinalIgnoreCase) ||
            app.DesktopRegistration is not { IsMsi: false } registration ||
            !string.Equals(registration.RegistryKeyName, ApkInstallerId, StringComparison.OrdinalIgnoreCase) ||
            GetCommandExecutable(registration.Command) is not { } uninstaller ||
            GetDisplayIconExecutable(registration.DisplayIcon) is not { } launcher)
            return false;

        return string.Equals(Path.GetFileName(uninstaller), "Uninstall APK.exe", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetFileName(launcher), "APK.exe", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetDirectoryName(uninstaller), Path.GetDirectoryName(launcher), StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetDisplayIconExecutable(string? icon)
    {
        var separator = icon?.LastIndexOf(',') ?? -1;
        if (separator >= 0 && int.TryParse(icon.AsSpan(separator + 1), out _))
            icon = icon![..separator];
        return NormalizePath(icon);
    }

    private static bool HasApkStartApprovalResidual(RegistryKey root, string installPath)
    {
        using var run = TryOpen(root, RunKey);
        var command = run?.GetValue(ApkDisplayName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (command is not null && !ReferencesInstallation(command, installPath))
            return false;

        using var approved = TryOpen(root, StartupApprovedRunKey);
        return approved?.GetValue(ApkDisplayName) is byte[] { Length: 12 };
    }
}
