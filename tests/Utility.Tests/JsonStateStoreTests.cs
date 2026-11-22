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
    public async Task PreservesDistinctMultiChannelFanDraftsPerProfile()
    {
        var store = new JsonStateStore(_tempDir);
        var daily = ProfileSeedFactory.CreatePristineReference(ProfileSeedFactory.DailyId);
        var gaming = ProfileSeedFactory.CreatePristineReference(ProfileSeedFactory.GamingId);
        var cpu = new FanChannelCurve(1, "Fan 1", "CPU", [new(40, 0, "Level"), new(60, 2, "Level")],
            0, 125, 1, 0, int.MaxValue, 1, "Level");
        var gpu = new FanChannelCurve(2, "Fan 2", "GPU", [new(40, 0, "RPM"), new(60, 1800, "RPM")],
            0, 125, 1, 0, int.MaxValue, 1);

        await store.SaveSettingsAsync(new AppSettingsState
        {
            Profiles = [daily with { FanCurve = new(FanCurveStatus.Captured, "Manual draft", [cpu, gpu]) }, gaming]
        });

        var loaded = (await store.LoadSettingsAsync()).Settings.Profiles;
        Assert.Equal(FanCurveStatus.Captured, loaded[0].FanCurve.Status);
        Assert.Equal(["Level", "RPM"], loaded[0].FanCurve.Channels.Select(channel => channel.SpeedUnit));
        Assert.Equal(0, loaded[0].FanCurve.Channels[1].Points[0].FanSpeedValue);
        Assert.Equal(FanCurveStatus.PendingCapture, loaded[1].FanCurve.Status);
        Assert.Empty(loaded[1].FanCurve.Channels);
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
