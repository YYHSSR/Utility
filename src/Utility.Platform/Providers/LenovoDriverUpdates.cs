using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;
using Utility.Core.Models;
using Utility.Platform.Lenovo;
using Utility.Platform.Native;

namespace Utility.Platform.Providers;

public sealed class LenovoDriverUpdates
{
    private static readonly Uri Catalog = new("https://download.lenovo.com/catalog/83JJ_Win11.xml");
    private const string PendingKey = @"SOFTWARE\Utility\LenovoDriverCleanup";
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(25)
    };

    private sealed record CatalogEntry(Uri Manifest, string Hash);
    private sealed record Rule(string[] HardwareIds, Version Version);
    private sealed record Target(InstalledDriverEntry Device, Version Version);
    private sealed record Replacement(string InstanceId, string PreviousInf, Version PreviousVersion, Version ExpectedVersion);
    private sealed record PreviousPackage(string Inf, string Hash);
    private sealed record PendingCleanup(string? MustRestartFrom, Replacement[] Devices, PreviousPackage[] Packages);
    private sealed record Package(string Id, string Title, string Date, string ManifestHash, Uri Installer, string InstallerHash,
        long Size, XElement Dependencies, Rule[] Rules, bool NvidiaInstaller);

    public async Task<LenovoDriverScan> ScanAsync(CancellationToken ct)
    {
        RequireSupportedComputer();
        var inventory = ReadDevices(ct);
        var entries = await LoadCatalogAsync(ct).ConfigureAwait(false);
        var updates = new ConcurrentBag<LenovoDriverUpdate>();
        var matched = 0;
        var errors = 0;
        await Parallel.ForEachAsync(entries, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (entry, token) =>
            {
                try
                {
                    var package = await LoadPackageAsync(entry, token).ConfigureAwait(false);
                    if (package is null || GetTargets(package, inventory) is not { Length: > 0 } targets)
                        return;
                    Interlocked.Increment(ref matched);
                    if (NeedsUpdate(targets))
                        updates.Add(new(package.Id, package.Title, JoinVersions(targets.Select(target => target.Device.DriverVersion)),
                            JoinVersions(targets.Select(target => target.Version.ToString())), package.Date, package.ManifestHash));
                }
                catch (Exception ex) when (!token.IsCancellationRequested &&
                    ex is HttpRequestException or IOException or InvalidDataException or XmlException or OperationCanceledException)
                {
                    Interlocked.Increment(ref errors);
                }
            }).ConfigureAwait(false);
        return new(updates.OrderBy(update => update.Title, StringComparer.CurrentCultureIgnoreCase).ToArray(), matched, errors);
    }

    public Task<ElevatedResponse> InstallAsync(LenovoDriverUpdate update, CancellationToken ct) =>
        new ElevatedWorkerClient().ExecuteRequestAsync(new ElevatedRequest
        {
            AuthToken = "",
            Operation = ElevatedOperation.InstallLenovoDriver,
            LenovoPackageId = update.Id,
            LenovoManifestHash = update.ManifestHash
        }, ct);

    public static bool HasPendingCleanup()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PendingKey);
            return key?.ValueCount > 0;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    public Task<ElevatedResponse> CleanupAsync() => new ElevatedWorkerClient().ExecuteRequestAsync(new ElevatedRequest
    {
        AuthToken = "",
        Operation = ElevatedOperation.CleanupLenovoDrivers
    });

    // The worker gets an ID and catalog hash, and obtains all paths, commands and device evidence independently.
    public async Task<LenovoDriverInstallResult> InstallInWorkerAsync(string id, string manifestHash, CancellationToken ct)
    {
        if (!LenovoWmiProvider.IsProcessElevated())
            throw new UnauthorizedAccessException("Administrator approval is required to install drivers.");
        RequireSupportedComputer();
        if (!ValidHash(manifestHash) || !SafeToken(id))
            throw new InvalidDataException("Invalid Lenovo catalog selection.");
        var entries = await LoadCatalogAsync(ct).ConfigureAwait(false);
        var entry = entries.SingleOrDefault(candidate => candidate.Hash.Equals(manifestHash, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("The Lenovo catalog has changed. Check for updates again.");
        var package = await LoadPackageAsync(entry, ct).ConfigureAwait(false);
        if (package is null || package.Id != id || GetTargets(package, ReadDevices(ct)) is not { Length: > 0 } targets ||
            !NeedsUpdate(targets))
            throw new InvalidDataException("The Lenovo driver is no longer applicable as an update.");

        var stage = CreateProtectedStage();
        try
        {
            var installer = Path.Combine(stage.FullName, Path.GetFileName(package.Installer.LocalPath));
            await using (var file = new FileStream(installer, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
                await DownloadAsync(package.Installer, file, package.Size, ct).ConfigureAwait(false);
            var signed = WinTrustInterop.InspectLocalFile(installer);
            if (!signed.IsSignatureValid || signed.FileSizeBytes != package.Size ||
                !signed.Sha256Hex.Equals(package.InstallerHash, StringComparison.OrdinalIgnoreCase) ||
                signed.SignerSubject?.Contains("Lenovo", StringComparison.OrdinalIgnoreCase) != true ||
                signed.PeArchitecture is not ("x64" or "x86"))
                throw new InvalidDataException("The downloaded package failed Lenovo signature, hardware architecture or hash verification.");

            // The disk location and flags are supplied here. Catalog command strings are checked, never passed to a shell.
            var extracted = stage.CreateSubdirectory("payload").FullName;
            var extractExit = await RunAsync(installer, stage.FullName,
                ["/VERYSILENT", "/NORESTART", "/DIR=" + extracted, "/EXTRACT=YES"]).ConfigureAwait(false);
            if (extractExit != 0)
                throw new IOException($"Lenovo package extraction failed ({extractExit}).");

            var inner = Path.Combine(extracted, package.NvidiaInstaller ? "nvsetup.exe" : Path.GetFileName(installer));
            if (!File.Exists(inner) || !DiskCleanupService.HasSafeParents(inner))
                throw new IOException("The extracted Lenovo installer does not match the catalog.");
            targets = GetTargets(package, ReadDevices(ct));
            if (!NeedsUpdate(targets))
                throw new InvalidDataException("The target device changed during download. Check for updates again.");
            var before = GetPreviousPackages(targets);
            var previous = before.Select(inf => new PreviousPackage(inf, ReadInfHash(inf) ?? ""))
                .Where(item => ValidHash(item.Hash)).ToArray();
            var replacements = targets.Select(target => new Replacement(target.Device.DeviceInstanceId!, target.Device.InfName,
                Version.Parse(target.Device.DriverVersion), target.Version)).ToArray();
            var boot = LenovoWmiProvider.ReadBootSession();
            var exit = await RunAsync(inner, extracted, package.NvidiaInstaller
                ? ["-n", "-s"]
                : ["/VERYSILENT", "/NORESTART", "/DIR=" + Path.Combine(extracted, "TMP")]).ConfigureAwait(false);
            if (exit != 0 && exit != 3010 && !(package.NvidiaInstaller && exit == 1))
                throw new IOException($"Lenovo driver installation returned {exit}.");

            var pending = new PendingCleanup(exit == 3010 ? boot ?? "unknown" : null, replacements, previous);
            if (previous.Length > 0)
            {
                using var records = Registry.LocalMachine.CreateSubKey(PendingKey);
                records.SetValue(package.ManifestHash, JsonSerializer.Serialize(pending), RegistryValueKind.String);
            }
            if (exit == 3010)
                return new(false, true, 0, before.Length);
            for (var attempt = 0; attempt < 5; attempt++)
            {
                if (TargetsAreActive(replacements, ReadDevices(CancellationToken.None)))
                {
                    var removed = previous.Length == 0 ? 0 :
                        await CleanupRecordAsync(package.ManifestHash, pending, CancellationToken.None).ConfigureAwait(false);
                    return new(true, false, removed, before.Length - removed);
                }
                if (attempt < 4)
                    await Task.Delay(800).ConfigureAwait(false);
            }
            return new(false, false, 0, before.Length);
        }
        finally { RemoveStage(stage); }
    }

    public static async Task<LenovoDriverCleanupResult> CleanupPreviousDriversInWorkerAsync(CancellationToken ct)
    {
        if (!LenovoWmiProvider.IsProcessElevated())
            throw new UnauthorizedAccessException("Administrator approval is required to remove previous driver packages.");
        RequireSupportedComputer();
        using var records = Registry.LocalMachine.OpenSubKey(PendingKey);
        var removed = 0;
        foreach (var entry in records?.GetValueNames() ?? [])
        {
            ct.ThrowIfCancellationRequested();
            if (ValidHash(entry) && records?.GetValue(entry) is string json && json.Length <= 65536 &&
                JsonSerializer.Deserialize<PendingCleanup>(json) is { } pending)
                removed += await CleanupRecordAsync(entry, pending, ct).ConfigureAwait(false);
        }
        return new(removed, records?.ValueCount ?? 0);
    }

    private static async Task<int> CleanupRecordAsync(string key, PendingCleanup pending, CancellationToken ct)
    {
        if (pending.MustRestartFrom is { } prior &&
            (prior == "unknown" || LenovoWmiProvider.ReadBootSession() is not { } currentBoot || currentBoot == prior))
            return 0;

        var active = ReadDevices(ct);
        if (!TargetsAreActive(pending.Devices, active))
            return 0;
        var bound = ReadDevices(ct, presentOnly: false).Concat(active).Select(device => device.InfName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var remaining = new List<PreviousPackage>();
        var removed = 0;
        foreach (var item in pending.Packages)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsOemInf(item.Inf) || !ValidHash(item.Hash) || bound.Contains(item.Inf))
            {
                remaining.Add(item);
                continue;
            }
            var path = InfPath(item.Inf);
            if (!File.Exists(path))
            {
                removed++;
                continue;
            }
            if (!string.Equals(ReadInfHash(item.Inf), item.Hash, StringComparison.OrdinalIgnoreCase))
            {
                remaining.Add(item);
                continue;
            }

            // No /force and no /uninstall. Windows refuses removal if the package is still needed.
            await RunAsync(Path.Combine(Environment.SystemDirectory, "pnputil.exe"),
                Environment.SystemDirectory, ["/delete-driver", item.Inf]).ConfigureAwait(false);
            if (File.Exists(path))
                remaining.Add(item);
            else
                removed++;
        }
        using var records = Registry.LocalMachine.OpenSubKey(PendingKey, writable: true);
        if (remaining.Count == 0)
            records?.DeleteValue(key, throwOnMissingValue: false);
        else
            records?.SetValue(key, JsonSerializer.Serialize(pending with { Packages = remaining.ToArray() }));
        return removed;
    }

    private static string InfPath(string inf) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", inf);

    private static string? ReadInfHash(string inf)
    {
        try { return WinTrustInterop.ComputeSha256Hex(InfPath(inf)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static void RequireSupportedComputer()
    {
        if (!new LenovoWmiProvider().ReadMachineIdentity().IsTargetLegion83JJ ||
            !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) ||
            System.Runtime.InteropServices.RuntimeInformation.OSArchitecture != System.Runtime.InteropServices.Architecture.X64)
            throw new NotSupportedException("Lenovo 83JJ driver updates require the supported Lenovo model and Windows 11 x64.");
    }

    private static List<InstalledDriverEntry> ReadDevices(CancellationToken ct, bool presentOnly = true) =>
        DeviceDriverInventory.Read(WindowsDriverProvider.ClassifyDriver, ct, presentOnly);

    private static async Task<CatalogEntry[]> LoadCatalogAsync(CancellationToken ct)
    {
        var root = await ReadXmlAsync(Catalog, null, ct).ConfigureAwait(false);
        var entries = root.Elements("package").Where(item =>
            !Excluded(item.Element("category")?.Value ?? "") &&
            item.Element("checksum")?.Attribute("type")?.Value.Equals("sha256", StringComparison.OrdinalIgnoreCase) == true)
            .Select(item => (Address: item.Element("location")?.Value, Hash: item.Element("checksum")!.Value.Trim()))
            .Where(item => ValidHash(item.Hash) && Uri.TryCreate(item.Address, UriKind.Absolute, out var url) &&
                OfficialUrl(url, ".xml"))
            .Select(item => new CatalogEntry(new Uri(item.Address!), item.Hash)).Distinct().Take(100).ToArray();
        if (root.Name != "packages" || entries.Length == 0)
            throw new InvalidDataException("Lenovo returned an empty or unrecognized Windows 11 catalog.");
        return entries;
    }

    private static async Task<Package?> LoadPackageAsync(CatalogEntry entry, CancellationToken ct)
    {
        var root = await ReadXmlAsync(entry.Manifest, entry.Hash, ct).ConfigureAwait(false);
        var id = (string?)root.Attribute("id");
        var title = root.Element("Title")?.Elements("Desc").FirstOrDefault()?.Value.Trim();
        if (root.Name != "Package" || id is null || !SafeToken(id) || string.IsNullOrWhiteSpace(title) || Excluded(title) ||
            (string?)root.Attribute("hide") == "True" || (string?)root.Element("PackageType")?.Attribute("type") != "2" ||
            (string?)root.Element("DetectVersion")?.Attribute("type") != "Inf" || root.Element("Dependencies") is not { } dependencies)
            return null;

        var files = root.Element("Files")?.Element("Installer")?.Elements("File").ToArray();
        if (files is not { Length: 1 } || files[0].Element("Name")?.Value.Trim() is not { } name ||
            !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !SafeToken(name[..^4]) ||
            files[0].Element("CRC")?.Value.Trim() is not { } hash || !ValidHash(hash) ||
            !long.TryParse(files[0].Element("Size")?.Value, out var size) || size is <= 0 or > 5_000_000_000)
            return null;

        var installer = new Uri(entry.Manifest, name);
        var command = root.Element("Install")?.Elements("Cmdline").FirstOrDefault()?.Value.Trim();
        var nvidia = string.Equals(command, @"%PACKAGEPATH%\nvsetup.exe -n -s", StringComparison.OrdinalIgnoreCase);
        if (!OfficialUrl(installer, ".exe") || !string.Equals(root.Element("ExtractCommand")?.Value.Trim(),
            $"{name} /VERYSILENT /DIR=%PACKAGEPATH% /EXTRACT=\"YES\"", StringComparison.OrdinalIgnoreCase) ||
            (!nvidia && !string.Equals(command, $@"%PACKAGEPATH%\{name} /verysilent /DIR=%PACKAGEPATH%\TMP",
                StringComparison.OrdinalIgnoreCase)))
            return null;

        var rules = root.Element("DetectInstall")?.Descendants("_Driver").Select(ParseRule).OfType<Rule>().ToArray() ?? [];
        if (rules.Length == 0)
            return null;
        return new Package(id, title, root.Element("ReleaseDate")?.Value ?? "", entry.Hash, installer, hash, size,
            dependencies, rules, nvidia);
    }

    private static Rule? ParseRule(XElement node) =>
        Version.TryParse(node.Element("Version")?.Value.Trim().TrimEnd('^'), out var version) &&
        node.Elements("HardwareID").Select(element => element.Value.Trim()).Where(id => id.Length > 0).ToArray() is { Length: > 0 } ids
        ? new Rule(ids, version) : null;

    private static Target[] GetTargets(Package package, IReadOnlyList<InstalledDriverEntry> devices)
    {
        if (!package.Dependencies.Elements().Any() ||
            !package.Dependencies.Elements().All(rule => Applies(rule, devices)))
            return [];
        var targets = new List<Target>();
        foreach (var device in devices)
        {
            if (string.IsNullOrEmpty(device.DeviceInstanceId))
                continue;
            var versions = package.Rules.Where(rule => rule.HardwareIds.Any(id => MatchesHardware(device, id)))
                .Select(rule => rule.Version).Distinct().ToArray();
            if (versions.Length > 1 || (versions.Length == 1 && !Version.TryParse(device.DriverVersion, out _)))
                return []; // A bundled package cannot be identified safely for this device.
            if (versions.Length == 1)
                targets.Add(new Target(device, versions[0]));
        }
        return targets.ToArray();
    }

    private static bool Applies(XElement rule, IReadOnlyList<InstalledDriverEntry> devices) => rule.Name.LocalName switch
    {
        "And" => rule.Elements().Any() && rule.Elements().All(child => Applies(child, devices)),
        "Or" => rule.Elements().Any(child => Applies(child, devices)),
        "_OS" => rule.Elements("OS").Any(os => os.Value.Trim().Equals("WIN11", StringComparison.OrdinalIgnoreCase)),
        "_WindowsBuildVersion" => MatchesVersion(rule.Element("Version")?.Value, new Version(Environment.OSVersion.Version.Build, 0)),
        "_PnPID" => devices.Any(device => MatchesHardware(device, rule.Value.Trim())),
        "_Driver" => devices.Any(device =>
            rule.Elements("HardwareID").Any(id => MatchesHardware(device, id.Value.Trim())) &&
            Version.TryParse(device.DriverVersion, out var version) && MatchesVersion(rule.Element("Version")?.Value, version) &&
            MatchesDate(rule.Element("Date")?.Value, device.DriverDate)),
        _ => false
    };

    private static bool MatchesVersion(string? expression, Version installed)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return false;
        var text = expression.Trim();
        var minimum = text.EndsWith('^');
        var number = text.TrimEnd('^');
        if (!Version.TryParse(number.Contains('.') ? number : number + ".0", out var required))
            return false;
        return minimum ? installed >= required : installed == required;
    }

    private static bool MatchesDate(string? expression, string installed)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return true;
        var value = expression.Trim();
        return DateOnly.TryParseExact(value.TrimEnd('^'), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expected) &&
            DateOnly.TryParseExact(installed, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var actual) &&
            (value.EndsWith('^') ? actual >= expected : actual == expected);
    }

    private static bool MatchesHardware(InstalledDriverEntry device, string id) => id.Length > 0 &&
        device.HardwareIds.Any(actual => actual.Equals(id, StringComparison.OrdinalIgnoreCase));

    private static bool NeedsUpdate(Target[] targets) => targets.Length > 0 &&
        targets.All(target => Version.Parse(target.Device.DriverVersion) <= target.Version) &&
        targets.Any(target => Version.Parse(target.Device.DriverVersion) < target.Version);

    private static bool TargetsAreActive(Replacement[] targets, IReadOnlyList<InstalledDriverEntry> active) =>
        targets.Length > 0 && targets.All(target => active.Any(device => device.IsHealthy &&
            string.Equals(device.DeviceInstanceId, target.InstanceId, StringComparison.OrdinalIgnoreCase) &&
            Version.TryParse(device.DriverVersion, out var version) && version >= target.ExpectedVersion &&
            (target.PreviousVersion == target.ExpectedVersion ||
             !device.InfName.Equals(target.PreviousInf, StringComparison.OrdinalIgnoreCase))));

    private static string[] GetPreviousPackages(Target[] targets) => targets.Where(target =>
            Version.Parse(target.Device.DriverVersion) < target.Version &&
            IsOemInf(target.Device.InfName))
        .Select(target => target.Device.InfName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static bool IsOemInf(string name) => Regex.IsMatch(name, @"\Aoem[0-9]+\.inf\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string JoinVersions(IEnumerable<string> versions) => string.Join(" / ", versions.Distinct());
    private static bool SafeToken(string value) => value.Length is > 0 and <= 120 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
    private static bool ValidHash(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static bool Excluded(string text) => new[] { "BIOS", "UEFI", "Firmware", "Software and Utilities", "Diagnostic" }
        .Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));
    private static bool OfficialUrl(Uri url, string extension) => url.IsAbsoluteUri && url.Scheme == Uri.UriSchemeHttps &&
        url.Host.Equals(Catalog.Host, StringComparison.OrdinalIgnoreCase) && url.IsDefaultPort &&
        url.Query.Length == 0 && url.Fragment.Length == 0 && url.UserInfo.Length == 0 &&
        url.AbsolutePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase);

    private static async Task<XElement> ReadXmlAsync(Uri url, string? expectedHash, CancellationToken ct)
    {
        using var data = new MemoryStream();
        await DownloadAsync(url, data, 2_000_000, ct).ConfigureAwait(false);
        if (expectedHash is not null &&
            !Convert.ToHexString(SHA256.HashData(data.GetBuffer().AsSpan(0, (int)data.Length)))
                .Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Lenovo manifest checksum does not match the machine catalog.");
        data.Position = 0;
        using var xml = XmlReader.Create(data, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 2_000_000 });
        return XElement.Load(xml);
    }

    private static async Task DownloadAsync(Uri uri, Stream destination, long limit, CancellationToken ct)
    {
        using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > limit)
            throw new InvalidDataException("Lenovo download exceeds the catalog size.");
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[128 * 1024];
        long length = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            length += read;
            if (length > limit)
                throw new InvalidDataException("Lenovo download exceeds the catalog size.");
            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
    }

    private static DirectoryInfo CreateProtectedStage()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
        if (!DiskCleanupService.HasSafeParents(root))
            throw new IOException("Windows temporary directory is redirected.");
        var path = Path.Combine(root, $"UtilityLenovoDriver-{Guid.NewGuid():N}");
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var security = new DirectorySecurity();
        security.SetOwner(admins);
        security.SetAccessRuleProtection(true, false);
        foreach (var identity in new[] { admins, system })
            security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return security.CreateDirectory(path);
    }

    private static void RemoveStage(DirectoryInfo stage)
    {
        try
        {
            if (!DiskCleanupService.HasSafeParents(stage.FullName))
                return;
            var folders = new Stack<DirectoryInfo>();
            folders.Push(stage);
            while (folders.TryPop(out var folder))
            {
                foreach (var child in folder.EnumerateDirectories())
                {
                    if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
                        return;
                    folders.Push(child);
                }
            }
            stage.Delete(recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static async Task<int> RunAsync(string executable, string directory, string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Windows did not start the driver operation.");
        await process.WaitForExitAsync().ConfigureAwait(false);
        return process.ExitCode;
    }
}
