using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Utility.Core.Models;

namespace Utility.Platform.Providers;

public sealed record UninstallTarget(
    InstalledAppEntry App, bool CanRemove, string? PackageName = null, string? PackageFullName = null,
    string? PackageInstallLocation = null, string? PackageAppId = null)
{
    /// <summary>Name to show (Start menu / manifest name for Store apps). Display only: matching uses App.DisplayName.</summary>
    public string? FriendlyName { get; init; }
    /// <summary>Publisher to show (manifest PublisherDisplayName, or the CN of the signing subject).</summary>
    public string? FriendlyPublisher { get; init; }
    /// <summary>Runtime, redistributable, codec/extension or other component rather than an app people launch.</summary>
    public bool IsComponent { get; init; }
    public IReadOnlyList<UninstallClassRegistration>? ResidualClasses { get; init; }
    public DesktopUninstallEvidence? DesktopEvidence { get; init; }
    public string? CapturedInstallLocation { get; init; }
    public int? DesktopExitCode { get; init; }
}

public sealed record DesktopUninstallEvidence(string RegistrationFingerprint, string[] Launchers);

public sealed record UninstallInventory(IReadOnlyList<UninstallTarget> Apps, string? StoreError);

public sealed record AppRemovalResult(bool Removed, bool RestartRequired, string? Detail)
{
    public int? DesktopExitCode { get; init; }
}

public sealed partial class WindowsUninstallService
{
    internal const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private sealed record AppxEntry(string Name, string PackageFullName, string PackageFamilyName,
        string? Publisher, string? Version, bool NonRemovable, bool IsBundle, string? InstallLocation, string? AppId,
        string? StartName = null, string? PackageDisplayName = null, string? PackagePublisherName = null);

    /// <summary>Desktop entries that are runtimes / redistributables / SDK parts rather than apps.</summary>
    private static readonly string[] ComponentNameParts =
    [
        "Redistributable", "Visual C++", "Runtime", "Update Health Tools", "Software Development Kit", "Windows SDK",
        "Targeting Pack", "Microsoft .NET", "Host FX Resolver", "WinAppDeploy", "Python Launcher", "Launcher for Windows",
        "Driver Package", "Microsoft Edge WebView2", "Windows Driver Package"
    ];

    internal static bool IsDesktopComponent(string name) =>
        ComponentNameParts.Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase));

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
        .Where(app => CreateDesktopStartInfo(app.DesktopRegistration) is not null)
        .Select(app => new UninstallTarget(app, true) { IsComponent = IsDesktopComponent(app.DisplayName) }).ToList();

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

        var exitCode = process.ExitCode;
        var restart = exitCode is 1641 or 3010;
        var removed = await IsUninstalledAsync(selected, CancellationToken.None).ConfigureAwait(false);
        var detail = !removed ? $"The uninstall entry is still present (exit code {exitCode})."
            : exitCode is 0 or 1641 or 3010 ? null : $"Uninstaller exit code {exitCode}.";
        return new(removed, restart, detail) { DesktopExitCode = exitCode };
    }

    public Task<bool> IsUninstalledAsync(UninstallTarget target, CancellationToken ct) =>
        Task.FromResult(IsUninstalled(target, ct));

    internal bool IsUninstalled(UninstallTarget target, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (target.PackageName is not null)
            return IsPackageFamilyAbsent(target);

        var registration = target.App.DesktopRegistration ?? throw new ArgumentException("Desktop registration is missing.");
        var (hive, view) = UninstallRegistryLocation(target.App.Scope);
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var entry = baseKey.OpenSubKey($@"{UninstallKey}\{registration.RegistryKeyName}");
        return entry is null;
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
            "$apps=@{}; foreach($s in (Get-StartApps -ErrorAction SilentlyContinue)){ " +
            "if($s.AppID -match '^([^!]+)!' -and !$apps.ContainsKey($Matches[1])){$apps[$Matches[1]]=$s} }; " +
            // Localized names as Settings shows them (resolves strings kept in language resource packages).
            "$names=@{}; try { $null=[Windows.Management.Deployment.PackageManager,Windows.Management.Deployment,ContentType=WindowsRuntime]; " +
            "foreach($p in (New-Object Windows.Management.Deployment.PackageManager).FindPackagesForUser('')){ " +
            "try { $names[$p.Id.FamilyName]=@($p.DisplayName,$p.PublisherDisplayName) } catch {} } } catch {}; " +
            "$items=@(Get-AppxPackage -PackageTypeFilter Main,Bundle | " +
            "Select-Object Name,PackageFullName,PackageFamilyName,Publisher,@{n='Version';e={[string]$_.Version}},NonRemovable,IsBundle,InstallLocation," +
            "@{n='AppId';e={$apps[$_.PackageFamilyName].AppID}},@{n='StartName';e={$apps[$_.PackageFamilyName].Name}}," +
            "@{n='PackageDisplayName';e={$names[$_.PackageFamilyName][0]}},@{n='PackagePublisherName';e={$names[$_.PackageFamilyName][1]}}); " +
            "ConvertTo-Json -InputObject $items -Compress";
        var result = await RunPowerShellAsync(command, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new IOException($"Could not enumerate current-user Appx packages. {result.Error.Trim()}");

        return (JsonSerializer.Deserialize<List<AppxEntry>>(result.Output) ?? [])
            .GroupBy(item => item.PackageFamilyName, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var main = group.FirstOrDefault(item => !item.IsBundle);
                return group.FirstOrDefault(item => item.IsBundle) is { } bundle
                    ? bundle with
                    {
                        InstallLocation = main?.InstallLocation ?? bundle.InstallLocation,
                        NonRemovable = bundle.NonRemovable || main?.NonRemovable == true
                    }
                    : main!;
            })
            .Where(item => !item.NonRemovable)
            .Select(item =>
            {
                // PackageManager names first (localized, resolved); the manifest is the fallback when they are missing.
                var (manifestName, manifestPublisher) = item.PackageDisplayName is { Length: > 0 } && item.PackagePublisherName is { Length: > 0 }
                    ? (item.PackageDisplayName, item.PackagePublisherName)
                    : PackageStrings(item);
                return new UninstallTarget(
                    new InstalledAppEntry($"StoreAppx:{item.PackageFullName}",
                        item.Name.Equals("Microsoft.WindowsStore", StringComparison.OrdinalIgnoreCase) ? "Microsoft Store" : item.Name,
                        item.Publisher ?? string.Empty, item.Version ?? string.Empty, InstallScope.StoreAppx, item.PackageFamilyName, true),
                    true, item.Name, item.PackageFullName, item.InstallLocation, item.AppId)
                {
                    FriendlyName = !string.IsNullOrWhiteSpace(item.StartName) ? item.StartName
                        : !string.IsNullOrWhiteSpace(item.PackageDisplayName) ? item.PackageDisplayName : manifestName,
                    FriendlyPublisher = manifestPublisher ?? SubjectCommonName(item.Publisher),
                    // No Start menu entry: an extension, codec, source or shell add-on rather than an app.
                    IsComponent = string.IsNullOrWhiteSpace(item.AppId)
                };
            })
            .ToList();
    }

    /// <summary>DisplayName / PublisherDisplayName from the package manifest, with ms-resource strings resolved.</summary>
    private static (string? Name, string? Publisher) PackageStrings(AppxEntry item)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(item.InstallLocation))
                return (null, null);
            var manifest = Path.Combine(item.InstallLocation, "AppxManifest.xml");
            if (!File.Exists(manifest))
                return (null, null);
            var properties = System.Xml.Linq.XDocument.Load(manifest).Root?.Elements()
                .FirstOrDefault(element => element.Name.LocalName == "Properties");
            string? Read(string name) => Resolve(item, properties?.Elements()
                .FirstOrDefault(element => element.Name.LocalName == name)?.Value);
            return (Read("DisplayName"), Read("PublisherDisplayName"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return (null, null);
        }
    }

    private static string? Resolve(AppxEntry item, string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (!value.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
            return value;

        var key = value["ms-resource:".Length..];
        var candidates = key.StartsWith("//", StringComparison.Ordinal)
            ? [value]
            : key.StartsWith('/')
                ? new[] { $"ms-resource://{item.Name}{key}" }
                : new[] { $"ms-resource://{item.Name}/resources/{key}", $"ms-resource://{item.Name}/{key}" };
        var buffer = new StringBuilder(512);
        foreach (var candidate in candidates)
        {
            buffer.Clear();
            if (SHLoadIndirectString("@{" + item.PackageFullName + "?" + candidate + "}", buffer, buffer.Capacity, IntPtr.Zero) == 0 &&
                buffer.Length > 0 && !buffer.ToString().StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                return buffer.ToString();
        }
        return null;
    }

    /// <summary>"CN=Microsoft Corporation, O=…" → "Microsoft Corporation"; a bare GUID-like CN is dropped.</summary>
    internal static string? SubjectCommonName(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
            return null;
        string? Part(string prefix) => subject.Split(',').Select(part => part.Trim())
            .FirstOrDefault(part => part.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..].Trim('"', ' ');
        var name = Part("CN=");
        if (name is null || Guid.TryParse(name, out _))
            name = Part("O=");
        return string.IsNullOrWhiteSpace(name) || Guid.TryParse(name, out _) ? null : name;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHLoadIndirectString(string source, StringBuilder output, int capacity, IntPtr reserved);

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
