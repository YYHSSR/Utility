using Utility.Core.Models;
using Utility.Core.Services;
using Xunit;

namespace Utility.Tests;

public sealed class JsonStateStoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"UtilityTests_{Guid.NewGuid():N}");

    [Fact]
    public async Task RecoversFromLastGoodBackupWhenPrimarySettingsCorrupted()
    {
        var store = new JsonStateStore(_tempDir);
        var initial = new AppSettingsState { ActiveSelectedProfileId = ProfileSeedFactory.GamingId };
        await store.SaveSettingsAsync(initial);

        await File.WriteAllTextAsync(store.SettingsPath, "{ invalid json !!!");
        var loaded = await store.LoadSettingsAsync();

        Assert.True(loaded.RecoveredFromLastGood);
        Assert.Equal(ProfileSeedFactory.GamingId, loaded.Settings.ActiveSelectedProfileId);
        Assert.Equal(1, loaded.Settings.SchemaVersion);
    }

    [Fact]
    public async Task RedactsSensitiveTokensAndBoundsDiagnosticsLog()
    {
        var store = new JsonStateStore(_tempDir);
        for (var i = 0; i < 510; i++)
        {
            await store.AppendDiagnosticAsync("TEST", $"Entry {i} SerialNumber=PF999999 Bearer abcdef12345");
        }

        var lines = await File.ReadAllLinesAsync(store.DiagnosticsLogPath);
        Assert.Equal(500, lines.Length);
        Assert.DoesNotContain(lines, l => l.Contains("PF999999") || l.Contains("abcdef12345"));
        Assert.All(lines, l => Assert.Contains("[REDACTED]", l));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
