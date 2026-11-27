using Microsoft.Win32;
using Utility.Core.Models;
using Utility.Platform.Providers;
using Xunit;

namespace Utility.Tests;

public sealed class UninstallResidualServiceTests
{
    [Fact]
    public async Task PartialDesktopUninstallRequiresMissingLaunchersAndUnchangedRegistrationBeforeCleanup()
    {
        var name = "FixtureApp" + Guid.NewGuid().ToString("N");
        var productId = name + "Desktop";
        var uninstallKey = $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{productId}";
        var protocolKey = $@"Software\Classes\{name}-url";
        var root = Directory.CreateDirectory(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), productId));
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var data = Directory.CreateDirectory(Path.Combine(roaming, name + " Desktop"));
        var other = Directory.CreateDirectory(Path.Combine(roaming, name + " Desktop Backup"));
        var profileState = Directory.CreateDirectory(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state", name));
        var launcher = Path.Combine(root.FullName, "app.exe");
        var installer = Path.Combine(root.FullName, "Update.exe");
        var installerCopy = Path.Combine(root.FullName, "installer-helper.exe");
        var surviving = Path.Combine(root.FullName, "other-app.exe");
        var userData = Path.Combine(data.FullName, "settings.json");
        var cachedState = Path.Combine(profileState.FullName, "state.json");
        var unrelated = Path.Combine(other.FullName, "keep.txt");
        var target = new UninstallTarget(new InstalledAppEntry($"CurrentUser:{productId}", name, "Fixture Publisher",
            "1.0", InstallScope.CurrentUser, null, false)
        {
            DesktopRegistration = new DesktopUninstallRegistration(productId, $"\"{installer}\" --uninstall",
                root.FullName, false, false)
        }, true);
        using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        var service = new UninstallResidualService(new WindowsUninstallService());
        string? backupFolder = null;
        try
        {
            File.WriteAllText(launcher, "App executable");
            File.WriteAllText(installer, "Installer executable");
            File.Copy(installer, installerCopy);
            File.WriteAllText(userData, "User settings");
            File.WriteAllText(cachedState, "User state");
            File.WriteAllText(unrelated, "Another product");
            using (var key = user.CreateSubKey(uninstallKey))
            {
                key.SetValue("DisplayName", name);
                key.SetValue("DisplayVersion", "1.0");
                key.SetValue("Publisher", "Fixture Publisher");
                key.SetValue("UninstallString", target.App.DesktopRegistration!.Command!);
                key.SetValue("InstallLocation", root.FullName);
            }
            using (var key = user.CreateSubKey(protocolKey))
            {
                key.SetValue("URL Protocol", "");
                using var command = key.CreateSubKey(@"shell\open\command");
                command.SetValue("", $"\"{launcher}\" \"%1\"");
            }

            var captured = await service.CaptureAsync(target, default);
            Assert.Equal(launcher, Assert.Single(captured.DesktopEvidence!.Launchers));
            captured = System.Text.Json.JsonSerializer.Deserialize<UninstallTarget>(
                System.Text.Json.JsonSerializer.Serialize(captured with { DesktopExitCode = 0 }))!;

            var running = await service.ScanAsync(captured, default);
            Assert.Equal(ResidualCleanupStatus.ReviewOnly, running.Status);
            Assert.Contains(running.Items, row => row.Path == launcher);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.RemoveAsync(captured, running.Items));

            File.Delete(launcher);
            Assert.Equal(ResidualCleanupStatus.ReviewOnly, (await service.ScanAsync(
                captured with { DesktopExitCode = 3010 }, default)).Status);
            File.WriteAllText(surviving, "Surviving app executable");
            Assert.Equal(ResidualCleanupStatus.ReviewOnly, (await service.ScanAsync(captured, default)).Status);
            File.Delete(surviving);

            var result = await service.ScanAsync(captured, default);
            Assert.Equal(ResidualCleanupStatus.StaleRegistration, result.Status);
            Assert.Contains(result.Items, row => string.Equals(row.Registry?.Key, uninstallKey, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(result.Items, row => row.Registry?.Key == protocolKey);
            Assert.Contains(result.Items, row => row.Path == installerCopy);
            Assert.Contains(result.Items, row => row.Path == userData && row.ContainsUserData);
            Assert.Contains(result.Items, row => row.Path == cachedState && row.ContainsUserData);
            Assert.DoesNotContain(result.Items, row => row.Path == unrelated);

            using (var key = user.OpenSubKey(uninstallKey, writable: true)!)
                key.SetValue("UnrelatedChange", "new");
            Assert.Equal(ResidualCleanupStatus.ReviewOnly, (await service.ScanAsync(captured, default)).Status);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.RemoveAsync(captured, result.Items));
            using (var key = user.OpenSubKey(uninstallKey, writable: true)!)
                key.DeleteValue("UnrelatedChange");

            var removed = await service.RemoveAsync(captured, result.Items.Where(row => !row.ContainsUserData).ToArray());
            backupFolder = removed.BackupFolder;
            Assert.Empty(removed.Errors);
            Assert.NotNull(backupFolder);
            Assert.Null(user.OpenSubKey(uninstallKey));
            Assert.Null(user.OpenSubKey(protocolKey));
            Assert.False(Directory.Exists(root.FullName));
            Assert.True(File.Exists(userData));
            Assert.True(File.Exists(cachedState));
            Assert.True(File.Exists(unrelated));

            var remaining = await service.ScanAsync(captured, default);
            Assert.Equal(ResidualCleanupStatus.Ready, remaining.Status);
            var dataRemoved = await service.RemoveAsync(captured, remaining.Items.Where(row => row.ContainsUserData).ToArray());
            Assert.Empty(dataRemoved.Errors);
            Assert.False(Directory.Exists(data.FullName));
            Assert.False(Directory.Exists(profileState.FullName));
            Assert.True(File.Exists(unrelated));
        }
        finally
        {
            user.DeleteSubKeyTree(uninstallKey, throwOnMissingSubKey: false);
            user.DeleteSubKeyTree(protocolKey, throwOnMissingSubKey: false);
            foreach (var directory in new[] { root.FullName, data.FullName, profileState.FullName, other.FullName, backupFolder })
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory!, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CapturedClassesFindOrphanedAssociationsAndOnlyEmptyOwnedSandboxes()
    {
        var id = Guid.NewGuid().ToString("D");
        var suffix = Guid.NewGuid().ToString("N");
        var name = $"UtilityResidualFixture-{suffix}";
        var protocol = $"alternate-{suffix}";
        var transferred = $"transferred-{suffix}";
        var progId = $"Utility.fixture.{suffix}";
        var extension = $".utility-fixture-{suffix}";
        var simpleExtension = $".utility-alone-{suffix}";
        var unrelated = $"Utility.other.{suffix}";
        const string classes = @"Software\Classes";
        const string commandPath = @"shell\open\command";
        var uninstallKey = $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{id}";
        var install = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, name));
        var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        var emptySandbox = Directory.CreateDirectory(Path.Combine(packages, name + ".sandbox.node"));
        var dataSandbox = Directory.CreateDirectory(Path.Combine(packages, name + ".sandbox.python"));
        var foreignSandbox = Directory.CreateDirectory(Path.Combine(packages, name + "-unrelated.sandbox.node"));
        var handler = $"\"{Path.Combine(install.FullName, "app.exe")}\" \"%1\"";
        var target = new UninstallTarget(new InstalledAppEntry($"CurrentUser:{id}", name, "Utility test fixture",
            "1.0.0", InstallScope.CurrentUser, null, false)
        {
            DesktopRegistration = new DesktopUninstallRegistration(id,
                $"\"{Path.Combine(install.FullName, "Uninstall.exe")}\" /currentuser", null, false, false)
        }, true);

        using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        var service = new UninstallResidualService(new WindowsUninstallService());
        string? backupFolder = null;
        try
        {
            using (var registration = user.CreateSubKey(uninstallKey))
            {
                registration.SetValue("DisplayName", name);
                registration.SetValue("DisplayVersion", "1.0.0");
                registration.SetValue("UninstallString", target.App.DesktopRegistration!.Command!);
            }
            foreach (var alias in new[] { protocol, transferred })
            {
                using var key = user.CreateSubKey($@"{classes}\{alias}");
                key.SetValue("", "URL:" + alias);
                key.SetValue("URL Protocol", "");
                using var command = key.CreateSubKey(commandPath);
                command.SetValue("", handler);
            }
            using (var command = user.CreateSubKey($@"{classes}\{progId}\{commandPath}"))
                command.SetValue("", handler);
            using (var fileType = user.CreateSubKey($@"{classes}\{extension}"))
            {
                fileType.SetValue("", progId);
                fileType.SetValue("Content Type", "application/octet-stream");
                using var openWith = fileType.CreateSubKey("OpenWithProgids");
                openWith.SetValue(progId, "");
                openWith.SetValue(unrelated, "");
            }
            using (var fileType = user.CreateSubKey($@"{classes}\{simpleExtension}"))
            {
                fileType.SetValue("", progId);
                using var openWith = fileType.CreateSubKey("OpenWithProgids");
            }
            emptySandbox.CreateSubdirectory(@"AC\Temp");
            File.WriteAllText(Path.Combine(dataSandbox.FullName, "keep.txt"), "User contents");

            var captured = await service.CaptureAsync(target, default);
            Assert.Equal(3, captured.ResidualClasses?.Count);
            user.DeleteSubKeyTree(uninstallKey);
            user.DeleteSubKeyTree($@"{classes}\{progId}");
            using (var command = user.OpenSubKey($@"{classes}\{protocol}\{commandPath}", writable: true)!)
                command.DeleteValue("");
            using (var command = user.OpenSubKey($@"{classes}\{transferred}\{commandPath}", writable: true)!)
                command.SetValue("", $"\"{Path.Combine(Environment.SystemDirectory, "notepad.exe")}\" \"%1\"");

            // A restarted Utility can rediscover the app-owned keys even after the uninstaller cleared their commands.
            var receipt = System.Text.Json.JsonSerializer.Serialize(captured);
            captured = System.Text.Json.JsonSerializer.Deserialize<UninstallTarget>(receipt)!;
            var result = await service.ScanAsync(captured, default);
            var sandbox = Assert.Single(result.Items, item => item.EmptyDirectoryTree);
            Assert.Equal(emptySandbox.FullName, sandbox.EmptyDirectory);
            Assert.DoesNotContain(result.Items, item => item.Registry?.Key.Contains(transferred) == true);
            Assert.Contains(result.Items, item => item.Registry?.Key == $@"{classes}\{protocol}" && item.Registry.Value is null);
            Assert.Contains(result.Items, item => item.Registry?.Key == $@"{classes}\{simpleExtension}" && item.Registry.Value is null);
            Assert.Contains(result.Items, item => item.Registry?.Key == $@"{classes}\{extension}" && item.Registry.Value == "");
            Assert.Contains(result.Items, item => item.Registry?.Key == $@"{classes}\{extension}\OpenWithProgids" && item.Registry.Value == progId);

            var removed = await service.RemoveAsync(captured, result.Items);
            backupFolder = removed.BackupFolder;
            Assert.Empty(removed.Errors);
            Assert.NotNull(removed.BackupFolder);
            Assert.False(Directory.Exists(emptySandbox.FullName));
            Assert.True(Directory.Exists(dataSandbox.FullName));
            Assert.True(Directory.Exists(foreignSandbox.FullName));
            Assert.Null(user.OpenSubKey($@"{classes}\{protocol}"));
            Assert.Null(user.OpenSubKey($@"{classes}\{simpleExtension}"));
            using var otherCommand = user.OpenSubKey($@"{classes}\{transferred}\{commandPath}");
            Assert.NotNull(otherCommand?.GetValue(""));
            using var retained = user.OpenSubKey($@"{classes}\{extension}");
            Assert.Null(retained?.GetValue(""));
            Assert.Equal("application/octet-stream", retained?.GetValue("Content Type"));
            using var keptOpenWith = retained?.OpenSubKey("OpenWithProgids");
            Assert.Equal(new[] { unrelated }, keptOpenWith?.GetValueNames());
        }
        finally
        {
            if (backupFolder is not null)
            {
                foreach (var file in Directory.EnumerateFiles(backupFolder, "*.reg"))
                    File.Delete(file);
                Directory.Delete(backupFolder);
            }
            user.DeleteSubKeyTree(uninstallKey, throwOnMissingSubKey: false);
            foreach (var alias in new[] { protocol, transferred, progId, extension, simpleExtension })
                user.DeleteSubKeyTree($@"{classes}\{alias}", throwOnMissingSubKey: false);
            foreach (var directory in new[] { install, emptySandbox, dataSandbox, foreignSandbox })
            {
                if (Directory.Exists(directory.FullName))
                    directory.Delete(recursive: true);
            }
        }
    }

    [Fact]
    public async Task VersionedAppWithoutInstallLocationFindsDataStartupAndEmptyCustomDirectory()
    {
        var name = "UtilityResidualFixture-" + Guid.NewGuid().ToString("N");
        var id = Guid.NewGuid().ToString("D");
        const string version = "0.6.4";
        const string runPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        var root = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, name));
        var empty = root.CreateSubdirectory(@"resources\runtime");
        var data = Directory.CreateDirectory(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), name));
        var marker = Path.Combine(data.FullName, "keep.txt");
        var deletedFile = Path.Combine(data.FullName, "delete.txt");
        var uninstaller = Path.Combine(root.FullName, $"Uninstall {name}.exe");
        var target = new UninstallTarget(new InstalledAppEntry($"CurrentUser:{id}", $"{name} {version}",
            "Utility test fixture", version, InstallScope.CurrentUser, null, false)
        {
            DesktopRegistration = new DesktopUninstallRegistration(id, $"\"{uninstaller}\" /currentuser", null, false, false)
        }, true);

        using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var run = user.CreateSubKey(runPath);
        using var installer = user.CreateSubKey(@"Software\" + id);
        var service = new UninstallResidualService(new WindowsUninstallService());
        try
        {
            File.WriteAllText(marker, "Keep the unselected user data.");
            File.WriteAllText(deletedFile, "Delete the selected read-only file.");
            File.SetAttributes(deletedFile, FileAttributes.ReadOnly);
            run.SetValue(name, $"\"{Path.Combine(root.FullName, name + ".exe")}\" --autostart");
            installer.SetValue("InstallLocation", root.FullName);
            var result = await service.ScanAsync(target, default);
            Assert.Contains(result.Items, row => row.File?.Path.Equals(marker, StringComparison.OrdinalIgnoreCase) == true);
            Assert.Contains(result.Items, row => row.Registry is { Value: var value } && value == name);
            Assert.Contains(result.Items, row => row.Registry?.Key == @"Software\" + id);

            var captured = await service.CaptureAsync(target, default);
            Assert.Equal(root.FullName, captured.CapturedInstallLocation);
            captured = System.Text.Json.JsonSerializer.Deserialize<UninstallTarget>(
                System.Text.Json.JsonSerializer.Serialize(captured))!;
            installer.DeleteValue("InstallLocation");
            Assert.Contains((await service.ScanAsync(captured, default)).Items, row => row.EmptyDirectory == empty.FullName);
            installer.SetValue("InstallLocation", root.FullName);

            var folder = Assert.Single(result.Items, row => row.EmptyDirectory == empty.FullName);
            var file = Assert.Single(result.Items, row => row.File?.Path.Equals(deletedFile, StringComparison.OrdinalIgnoreCase) == true);
            var removed = await service.RemoveAsync(target, [folder, file]);
            Assert.True(removed.Errors.Count == 0, string.Join(Environment.NewLine, removed.Errors));
            Assert.Contains(root.FullName, removed.Removed);
            Assert.False(Directory.Exists(root.FullName));
            Assert.False(File.Exists(deletedFile));
            Assert.True(File.Exists(marker));
            Assert.NotNull(run.GetValue(name));
        }
        finally
        {
            run.DeleteValue(name, throwOnMissingValue: false);
            installer.Dispose();
            user.DeleteSubKey(@"Software\" + id, throwOnMissingSubKey: false);
            File.Delete(marker);
            if (File.Exists(deletedFile))
            {
                File.SetAttributes(deletedFile, FileAttributes.Normal);
                File.Delete(deletedFile);
            }
            data.Delete();
            empty.Refresh();
            if (empty.Exists) empty.Delete();
            var parent = empty.Parent;
            if (parent?.Exists == true) parent.Delete();
            root.Refresh();
            if (root.Exists) root.Delete();
        }
    }

    [Fact]
    public async Task ApkReceiptFindsShortInstallPathAndEmptyParentWithoutInstallerRegistration()
    {
        const string id = "c99f334e-0eed-51c5-9914-8835e31e2d12";
        var fixture = Directory.CreateTempSubdirectory("Utility-ApkResidual-");
        try
        {
            var parent = fixture.CreateSubdirectory("APK");
            var install = parent.CreateSubdirectory("APK");
            var launcher = Path.Combine(install.FullName, "APK.exe");
            File.WriteAllText(launcher, "Simulated installed file");
            var target = new UninstallTarget(new InstalledAppEntry($"CurrentUser:{id}", "APK", "RoutineMiku",
                "0.6.10", InstallScope.CurrentUser, null, false)
            {
                DesktopRegistration = new DesktopUninstallRegistration(id,
                    $"\"{Path.Combine(install.FullName, "Uninstall APK.exe")}\" /currentuser",
                    null, false, false, $"\"{launcher}\",0")
            }, true) { DesktopExitCode = 0 };
            var service = new UninstallResidualService(new WindowsUninstallService());

            Assert.Contains((await service.ScanAsync(target, default)).Items, row => row.Path == launcher);
            var unrelated = target with { App = target.App with { Publisher = "Different publisher" } };
            Assert.DoesNotContain((await service.ScanAsync(unrelated, default)).Items, row => row.Path == launcher);

            File.Delete(launcher);
            install.Delete();
            Assert.Contains((await service.ScanAsync(target, default)).Items, row => row.EmptyDirectory == parent.FullName);

            File.WriteAllText(Path.Combine(parent.FullName, "keep.txt"), "A nonempty parent is excluded.");
            Assert.DoesNotContain((await service.ScanAsync(target, default)).Items, row => row.EmptyDirectory == parent.FullName);
        }
        finally
        {
            fixture.Delete(recursive: true);
        }
    }
}
