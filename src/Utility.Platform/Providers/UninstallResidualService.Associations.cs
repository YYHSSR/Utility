using Microsoft.Win32;

namespace Utility.Platform.Providers;

public sealed record UninstallClassRegistration(RegistryHive Hive, RegistryView View, string Name, bool IsProtocol, string? Label);

public sealed partial class UninstallResidualService
{
    private const string ClassesPath = @"Software\Classes";
    private const string OpenCommandPath = @"shell\open\command";
    private static readonly (RegistryHive Hive, RegistryView View)[] ClassLocations =
    [
        (RegistryHive.CurrentUser, RegistryView.Registry64),
        (RegistryHive.LocalMachine, RegistryView.Registry64),
        (RegistryHive.LocalMachine, RegistryView.Registry32)
    ];

    private static IEnumerable<UninstallClassRegistration> ReadOwnedClasses(
        UninstallTarget target, string installPath, CancellationToken ct)
    {
        var installation = WindowsUninstallService.UninstallRegistryLocation(target.App.Scope);
        foreach (var (hive, view) in ClassLocations)
        {
            if (hive != RegistryHive.CurrentUser && (hive, view) != installation)
                continue;
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var classes = TryOpen(root, ClassesPath);
            if (classes is null)
                continue;

            foreach (var name in classes.GetSubKeyNames())
            {
                ct.ThrowIfCancellationRequested();
                if (name.StartsWith('.'))
                    continue;
                using var command = TryOpen(classes, $@"{name}\{OpenCommandPath}");
                if (command is null || !ReferencesInstallation(ReadValue(command, ""), installPath) ||
                    !HasOnlyNames(command.GetValueNames(), "") || SafeSegment(name) is null)
                    continue;
                using var key = TryOpen(classes, name);
                if (key is null)
                    continue;
                var label = ReadValue(key, "") as string;
                if (label is null || label.Length <= 120)
                    yield return new UninstallClassRegistration(hive, view, name,
                        key.GetValueNames().Contains("URL Protocol", StringComparer.OrdinalIgnoreCase), label);
            }
        }
    }

    private static IEnumerable<AppResidual> ScanClassRegistrations(UninstallTarget target, string installPath, CancellationToken ct)
    {
        var installation = WindowsUninstallService.UninstallRegistryLocation(target.App.Scope);
        var registrations = ReadOwnedClasses(target, installPath, ct).Concat(target.ResidualClasses ?? [])
            .Where(item => SafeSegment(item.Name) is not null && !item.Name.StartsWith('.') &&
                ((item.Hive, item.View) == (RegistryHive.CurrentUser, RegistryView.Registry64) ||
                 (item.Hive, item.View) == installation))
            .DistinctBy(item => (item.Hive, item.View, item.Name.ToUpperInvariant())).ToArray();
        if (registrations.Length == 0)
            yield break;

        // A class can be registered at both user and machine scope. Leave associations for a surviving owner.
        var fileClasses = registrations.Where(item => !item.IsProtocol)
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => CanRemoveAssociation(group.Key, group, installPath))
            .Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (hive, view) in ClassLocations)
        {
            if (hive != RegistryHive.CurrentUser && (hive, view) != installation)
                continue;
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var classes = TryOpen(root, ClassesPath);
            if (classes is null)
                continue;

            foreach (var registration in registrations.Where(item => item.Hive == hive && item.View == view))
            {
                ct.ThrowIfCancellationRequested();
                using var key = TryOpen(classes, registration.Name);
                if (key is null)
                    continue;
                var path = $@"{ClassesPath}\{registration.Name}";
                if (IsSimpleAppClass(key, registration, installPath))
                {
                    if (CreateResidual(path, null, registration.IsProtocol ? "AppProtocol" : "FileAssociation") is { } residual)
                        yield return residual;
                }
                else
                {
                    using var command = TryOpen(key, OpenCommandPath);
                    if (command is not null && ReferencesInstallation(ReadValue(command, ""), installPath) &&
                        CreateResidual($@"{path}\{OpenCommandPath}", "", "InstallReferenceRegistry") is { } residual)
                        yield return residual;
                }
            }

            if (fileClasses.Count == 0)
                continue;
            foreach (var name in classes.GetSubKeyNames())
            {
                ct.ThrowIfCancellationRequested();
                if (!name.StartsWith('.') || name.Length < 2)
                    continue;
                using var extension = TryOpen(classes, name);
                if (extension is null)
                    continue;
                var path = $@"{ClassesPath}\{name}";
                using var openWith = TryOpen(extension, "OpenWithProgids");
                if (ReadValue(extension, "") is string progId && fileClasses.Contains(progId))
                {
                    var onlyOwned = extension.ValueCount == 1 && (extension.SubKeyCount == 0 ||
                        (extension.SubKeyCount == 1 && openWith is not null && openWith.SubKeyCount == 0 &&
                         openWith.GetValueNames().All(fileClasses.Contains)));
                    if (CreateResidual(path, onlyOwned ? null : "", "FileAssociation") is { } residual)
                        yield return residual;
                    if (onlyOwned)
                        continue;
                }
                if (openWith is null)
                    continue;
                foreach (var alias in openWith.GetValueNames().Where(fileClasses.Contains))
                {
                    if (CreateResidual($@"{path}\OpenWithProgids", alias, "FileAssociation") is { } residual)
                        yield return residual;
                }
            }

            AppResidual? CreateResidual(string path, string? value, string evidence) =>
                Fingerprint(root, path, value, ct) is { } stamp
                    ? new AppResidual(null, new RegistryResidual(hive, view, path, value, stamp), evidence)
                    : null;
        }
    }

    private static bool CanRemoveAssociation(string name, IEnumerable<UninstallClassRegistration> registrations, string installPath)
    {
        foreach (var (hive, view) in ClassLocations)
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var classes = TryOpen(root, ClassesPath);
            if (classes is null)
                continue;
            using var key = TryOpen(classes, name);
            if (key is null)
            {
                if (classes.GetSubKeyNames().Contains(name, StringComparer.OrdinalIgnoreCase))
                    return false;
                continue;
            }
            using var command = TryOpen(key, OpenCommandPath);
            if (command is not null && ReferencesInstallation(ReadValue(command, ""), installPath) &&
                HasOnlyNames(command.GetValueNames(), ""))
                continue;
            var previous = registrations.FirstOrDefault(item => item.Hive == hive && item.View == view);
            if (previous is null || !IsSimpleAppClass(key, previous, installPath))
                return false;
        }
        return true;
    }

    private static bool IsSimpleAppClass(RegistryKey key, UninstallClassRegistration registration, string installPath)
    {
        var protocol = key.GetValueNames().Contains("URL Protocol", StringComparer.OrdinalIgnoreCase);
        var label = ReadValue(key, "");
        if (protocol != registration.IsProtocol || !HasOnlyNames(key.GetSubKeyNames(), "DefaultIcon", "shell") ||
            !HasOnlyNames(key.GetValueNames(), "", "URL Protocol") ||
            (!IsBlank(label) && !Equals(label, registration.Label)))
            return false;

        if (key.GetSubKeyNames().Contains("DefaultIcon", StringComparer.OrdinalIgnoreCase))
        {
            using var icon = TryOpen(key, "DefaultIcon");
            if (icon is null || icon.SubKeyCount != 0 || !HasOnlyNames(icon.GetValueNames(), ""))
                return false;
            var value = ReadValue(icon, "");
            if (!IsBlank(value))
            {
                if (value is not string iconText)
                    return false;
                var index = iconText.LastIndexOf(',');
                var iconPath = NormalizePath(index < 0 ? iconText : iconText[..index]);
                if (iconPath is null || !DiskCleanupService.IsChild(iconPath, installPath))
                    return false;
            }
        }

        if (!key.GetSubKeyNames().Contains("shell", StringComparer.OrdinalIgnoreCase))
            return true;
        using var shell = TryOpen(key, "shell");
        if (shell is null || !HasOnlyNames(shell.GetSubKeyNames(), "open") || !HasOnlyNames(shell.GetValueNames(), "") ||
            (!IsBlank(ReadValue(shell, "")) && !string.Equals(ReadValue(shell, "") as string, "open", StringComparison.OrdinalIgnoreCase)))
            return false;
        if (shell.SubKeyCount == 0)
            return true;
        using var open = TryOpen(shell, "open");
        if (open is null || !HasOnlyNames(open.GetSubKeyNames(), "command") || !HasOnlyNames(open.GetValueNames(), ""))
            return false;
        if (open.SubKeyCount == 0)
            return true;
        using var command = TryOpen(open, "command");
        if (command is null || command.SubKeyCount != 0 || !HasOnlyNames(command.GetValueNames(), ""))
            return false;
        var handler = ReadValue(command, "");
        return IsBlank(handler) || ReferencesInstallation(handler, installPath);
    }

    private static object? ReadValue(RegistryKey key, string name) =>
        key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);

    private static bool IsBlank(object? value) => value is null || value is string text && string.IsNullOrWhiteSpace(text);

    private static bool HasOnlyNames(string[] names, params string[] allowed) =>
        names.All(name => allowed.Contains(name, StringComparer.OrdinalIgnoreCase));
}
