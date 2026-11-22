using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Utility.Core.Models;

namespace Utility.Platform.Providers;

public sealed record UninstallTarget(
    InstalledAppEntry App, bool CanRemove, string? PackageName = null, string? PackageFullName = null);

public sealed record UninstallInventory(IReadOnlyList<UninstallTarget> Apps, string? StoreError);

public sealed record AppRemovalResult(bool Removed, bool RestartRequired, string? Detail);

public sealed partial class WindowsUninstallService
{
    internal const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private sealed record AppxEntry(string Name, string PackageFullName, string PackageFamilyName,
        string? Publisher, string? Version, bool NonRemovable, bool IsBundle);

    private sealed record CommandResult(int ExitCode, string Output, string Error);

    private static readonly string SystemDirectory = Environment.SystemDirectory;
    private static readonly string PowerShellPath = Path.Combine(SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
    private static readonly HashSet<string> ScriptHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe",
        "mshta.exe", "rundll32.exe", "regsvr32.exe", "msiexec.exe", "explorer.exe"
    };

    public Task<UninstallInventory> ListAsync(CancellationToken ct) => ListAsync(ct, null);

    public async Task<UninstallInventory> ListAsync(CancellationToken ct, Func<IReadOnlyList<UninstallTarget>, Task>? showDesktop)
    {
        var desktopTask = Task.Run(async () =>
        {
            var apps = ReadDesktopTargets();
            if (showDesktop is not null)
                await showDesktop(apps).ConfigureAwait(false);
            return apps;
        }, ct);
        var storeTask = ListStoreAsync(ct);
        await Task.WhenAll(desktopTask, storeTask).ConfigureAwait(false);
        var store = await storeTask.ConfigureAwait(false);
        var entries = await desktopTask.ConfigureAwait(false);
        return new UninstallInventory(entries.Concat(store.Apps)
            .OrderBy(item => item.App.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray(), store.StoreError);
    }

    private static async Task<UninstallInventory> ListStoreAsync(CancellationToken ct)
    {
        try
        {
            return new UninstallInventory(await ReadAppxTargetsAsync(ct).ConfigureAwait(false), null);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or JsonException or TimeoutException)
        {
            return new UninstallInventory([], ex.Message);
        }
    }

    private static List<UninstallTarget> ReadDesktopTargets() => WindowsSoftwareProvider.ReadInstalledApps(includeStoreApps: false)
        .Select(app => new UninstallTarget(app, CreateDesktopStartInfo(app.DesktopRegistration) is not null)).ToList();

    public async Task<AppRemovalResult> UninstallAsync(UninstallTarget selected)
    {
        if (!selected.CanRemove)
            return new(false, false, "Windows does not report a usable uninstaller.");

        if (selected.PackageName is not null)
            return await RemoveAppxAsync(selected).ConfigureAwait(false);

        var fresh = await Task.Run(() =>
        {
            var app = selected.App;
            var key = app.DesktopRegistration?.RegistryKeyName;
            if (string.IsNullOrEmpty(key) || key.Contains('\\')) return null;
            var (hive, view) = UninstallRegistryLocation(app.Scope);
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var registration = root.OpenSubKey(UninstallKey + "\\" + key);
            return registration is null ? null : WindowsSoftwareProvider.ReadDesktopRegistration(registration, app.Scope, key);
        }).ConfigureAwait(false);
        var start = fresh == selected.App ? CreateDesktopStartInfo(fresh.DesktopRegistration) : null;
        if (start is null)
            return new(false, false, "Uninstall registration changed. Refresh the software list.");

        using var process = Process.Start(start) ?? throw new IOException("Windows did not start the uninstaller.");
        await process.WaitForExitAsync().ConfigureAwait(false);

        var restart = process.ExitCode is 1641 or 3010;
        if (!await IsUninstalledAsync(selected, CancellationToken.None).ConfigureAwait(false))
            return new(false, restart, $"The uninstall entry is still present (exit code {process.ExitCode}).");
        return new(true, restart, process.ExitCode is 0 or 1641 or 3010 ? null : $"Uninstaller exit code {process.ExitCode}.");
    }

    public Task<bool> IsUninstalledAsync(UninstallTarget target, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (target.PackageName is not null)
            return Task.FromResult(IsPackageFamilyAbsent(target));

        var registration = target.App.DesktopRegistration ?? throw new ArgumentException("Desktop registration is missing.");
        var (hive, view) = UninstallRegistryLocation(target.App.Scope);
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var entry = baseKey.OpenSubKey($@"{UninstallKey}\{registration.RegistryKeyName}");
        return Task.FromResult(entry is null);
    }

    private static bool IsPackageFamilyAbsent(UninstallTarget target)
    {
        var family = ValidatePackageToken(target.App.PackageFamilyName);
        uint count = 0, length = 0;
        var result = GetPackagesByPackageFamily(family, ref count, IntPtr.Zero, ref length, IntPtr.Zero);
        return result switch
        {
            0 => count == 0,
            122 when count > 0 => false, // The OS has package names to return.
            _ => throw new System.ComponentModel.Win32Exception(result)
        };
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetPackagesByPackageFamily(string family, ref uint count, IntPtr names, ref uint length, IntPtr buffer);

    internal static (RegistryHive Hive, RegistryView View) UninstallRegistryLocation(InstallScope scope) => scope switch
    {
        InstallScope.CurrentUser => (RegistryHive.CurrentUser, RegistryView.Registry64),
        InstallScope.MachineX86 => (RegistryHive.LocalMachine, RegistryView.Registry32),
        InstallScope.MachineX64 => (RegistryHive.LocalMachine, RegistryView.Registry64),
        _ => throw new ArgumentException("Unrecognized desktop installation scope.")
    };

    private static ProcessStartInfo? CreateDesktopStartInfo(DesktopUninstallRegistration? registration)
    {
        if (registration is null || registration.NoRemove || string.IsNullOrWhiteSpace(registration.Command))
            return null;

        var command = Environment.ExpandEnvironmentVariables(registration.Command.Trim());
        var executable = SplitExecutable(command);
        if (Guid.TryParseExact(registration.RegistryKeyName, "B", out var id) &&
            (registration.IsMsi || executable is { } msi &&
                Path.GetFileName(msi.Path).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) &&
                msi.Arguments.Contains(id.ToString("B"), StringComparison.OrdinalIgnoreCase)))
            return new ProcessStartInfo(Path.Combine(SystemDirectory, "msiexec.exe"), $"/x {id:B} /norestart") { UseShellExecute = true };

        if (executable is not { } split || !Path.IsPathFullyQualified(split.Path) ||
            !DiskCleanupService.IsFixedDrive(split.Path) || ScriptHosts.Contains(Path.GetFileName(split.Path)) ||
            !File.Exists(split.Path))
            return null;

        return new ProcessStartInfo(split.Path, split.Arguments)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(split.Path) ?? SystemDirectory
        };
    }

    internal static (string Path, string Arguments)? SplitExecutable(string command)
    {
        var match = ExecutablePattern().Match(command.Trim());
        return match.Success ? (match.Groups["path"].Value, match.Groups["args"].Value.Trim()) : null;
    }

    private static async Task<List<UninstallTarget>> ReadAppxTargetsAsync(CancellationToken ct)
    {
        var command = "$ErrorActionPreference='Stop'; [Console]::OutputEncoding=[Text.Encoding]::UTF8; " +
            "$items=@(Get-AppxPackage -PackageTypeFilter Main,Bundle | " +
            "Select-Object Name,PackageFullName,PackageFamilyName,Publisher,@{n='Version';e={[string]$_.Version}},NonRemovable,IsBundle); " +
            "ConvertTo-Json -InputObject $items -Compress";
        var result = await RunPowerShellAsync(command, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new IOException($"Could not enumerate current-user Appx packages. {result.Error.Trim()}");

        return (JsonSerializer.Deserialize<List<AppxEntry>>(result.Output) ?? [])
            .GroupBy(item => item.PackageFamilyName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.FirstOrDefault(item => item.IsBundle) ?? group.First())
            .Select(item => new UninstallTarget(
                new InstalledAppEntry($"StoreAppx:{item.PackageFullName}",
                    item.Name.Equals("Microsoft.WindowsStore", StringComparison.OrdinalIgnoreCase) ? "Microsoft Store" : item.Name,
                    item.Publisher ?? string.Empty, item.Version ?? string.Empty, InstallScope.StoreAppx, item.PackageFamilyName, true),
                !item.NonRemovable, item.Name, item.PackageFullName))
            .ToList();
    }

    private static async Task<AppRemovalResult> RemoveAppxAsync(UninstallTarget selected)
    {
        var name = ValidatePackageToken(selected.PackageName);
        var fullName = ValidatePackageToken(selected.PackageFullName);
        var command = "$ErrorActionPreference='Stop'; [Console]::OutputEncoding=[Text.Encoding]::UTF8; " +
            $"$pkg=Get-AppxPackage -PackageTypeFilter Main,Bundle -Name '{name}' | Where-Object {{$_.PackageFullName -eq '{fullName}'}}; " +
            "if (!$pkg -or $pkg.NonRemovable -or $pkg.IsFramework -or $pkg.IsResourcePackage) { throw 'The package cannot be removed on this installation.' }; " +
            "Remove-AppxPackage -Package $pkg.PackageFullName -Confirm:$false";
        var result = await RunPowerShellAsync(command, null, CancellationToken.None).ConfigureAwait(false);
        var removed = result.ExitCode == 0 &&
            IsPackageFamilyAbsent(selected);
        return new(removed, false, removed ? null : $"The package is still installed. {result.Error.Trim()}");
    }

    private static string ValidatePackageToken(string? token) =>
        token is not null && PackageToken().IsMatch(token) ? token : throw new ArgumentException("Invalid Windows package identifier.");

    private static async Task<CommandResult> RunPowerShellAsync(string command, TimeSpan? timeout, CancellationToken ct)
    {
        var start = new ProcessStartInfo(PowerShellPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(command)) })
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new IOException("Could not start Windows PowerShell.");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } duration)
            limit.CancelAfter(duration);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill();
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException("Windows package inventory did not respond in time.");
        }

        return new(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    [GeneratedRegex(@"^(?:""(?<path>[^""]+\.exe)""|(?<path>[^""]+?\.exe))(?=\s|$)(?<args>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExecutablePattern();

    [GeneratedRegex(@"\A[A-Za-z0-9_.-]{1,300}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PackageToken();
}
