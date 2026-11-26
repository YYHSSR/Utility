using Microsoft.Win32;
using Utility.Platform.Providers;
using Xunit;

namespace Utility.Tests;

public sealed class StartupGuardTests
{
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    /// <summary>
    /// Simulates an app (e.g. Electron's setLoginItemSettings) that flips its StartupApproved flag back to enabled
    /// after the user turned it off: the watcher must notice, and enforcement must switch it off again.
    /// </summary>
    [Fact]
    public async Task ReEnabledRunEntryIsNoticedAndSwitchedOffAgain()
    {
        var name = $"UtilityGuardFixture-{Guid.NewGuid():N}";
        var stableId = $"UserRunRegistry:HKCU\\...\\Run:{name}";
        using var run = Registry.CurrentUser.CreateSubKey(RunPath, writable: true);
        using var approved = Registry.CurrentUser.CreateSubKey(ApprovedPath, writable: true);
        try
        {
            run.SetValue(name, "\"C:\\NoSuch\\fixture.exe\" --autostart");
            Assert.True(WindowsSoftwareProvider.SetStartupEntryEnabledDirect(stableId, false).Success);
            Assert.True(IsDisabled(approved, name));

            // Already off: nothing to do.
            Assert.Empty(WindowsSoftwareProvider.EnforceDisabledStartupEntries([stableId]));

            var provider = new WindowsSoftwareProvider();
            using var changed = new SemaphoreSlim(0);
            using (provider.WatchStartupChanges(() => changed.Release()))
            {
                // The app turns itself back on, the way Electron writes it.
                approved.SetValue(name, new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
                Assert.True(await changed.WaitAsync(TimeSpan.FromSeconds(5)), "watcher did not report the change");
            }

            var redisabled = WindowsSoftwareProvider.EnforceDisabledStartupEntries([stableId]);
            Assert.Equal(name, Assert.Single(redisabled).Name);
            Assert.True(IsDisabled(approved, name));

            // An entry that is not on the kept-off list is left alone.
            approved.SetValue(name, new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
            Assert.Empty(WindowsSoftwareProvider.EnforceDisabledStartupEntries(["UserRunRegistry:HKCU\\...\\Run:SomethingElse"]));
            Assert.False(IsDisabled(approved, name));
        }
        finally
        {
            run.DeleteValue(name, throwOnMissingValue: false);
            approved.DeleteValue(name, throwOnMissingValue: false);
        }
    }

    private static bool IsDisabled(RegistryKey approved, string name) =>
        approved.GetValue(name) is byte[] { Length: >= 1 } bytes && (bytes[0] & 0x01) != 0;
}
