using Microsoft.Win32;
using Utility.Core.Models;
using Utility.Platform.Providers;
using Xunit;

namespace Utility.Tests;

public sealed class UninstallResidualServiceTests
{
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
}
