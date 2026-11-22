using Utility.Core.Models;
using Utility.Core.Services;
using Xunit;

namespace Utility.Tests;

public sealed class LenovoFanCaptureTests
{
    [Fact]
    public void DecodesSpeedLevelSeparatelyFromTemperatureStage()
    {
        var (cpu, gpu, auxiliary) = FirmwareTables();
        var levels = new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 9 };

        var capture = Assert.IsType<LenovoFanCapture>(LenovoFanCapture.From83JJTables(levels, cpu, gpu, auxiliary));

        Assert.Equal(LenovoFanCapture.OwnerReference.Stages, capture.Stages);
        Assert.Equal(87, capture.Stages[9].CpuTemperatureC);
        Assert.Equal(4300, capture.Stages[9].CpuRpm);
        Assert.Null(capture.Stages[9].AuxiliaryCpuTemperatureC);
        levels[9] = 0;
        Assert.Null(LenovoFanCapture.From83JJTables(levels, cpu, gpu, auxiliary));
    }

    [Fact]
    public async Task StoresAnExplicitCaptureWithoutAssigningItToBothProfiles()
    {
        var path = Path.Combine(Path.GetTempPath(), $"UtilityCapture_{Guid.NewGuid():N}");
        try
        {
            var store = new JsonStateStore(path);
            var settings = new AppSettingsState
            {
                Profiles = ProfileSeedFactory.CreateEditableSeeds().Select(profile =>
                    profile.Id == ProfileSeedFactory.GamingId
                        ? profile with { CapturedLegionFanTable = LenovoFanCapture.OwnerReference }
                        : profile).ToArray()
            };
            await store.SaveSettingsAsync(settings);
            var profiles = (await store.LoadSettingsAsync()).Settings.Profiles;
            Assert.Null(profiles.Single(profile => profile.Id == ProfileSeedFactory.DailyId).CapturedLegionFanTable);
            var gaming = profiles.Single(profile => profile.Id == ProfileSeedFactory.GamingId);
            Assert.Equal(FanCurveStatus.PendingCapture, gaming.FanCurve.Status);
            Assert.Equal(10, gaming.CapturedLegionFanTable?.Stages.Count);
        }
        finally
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
    }

    private static (FanCurvePoint[] Cpu, FanCurvePoint[] Gpu, FanCurvePoint[] Auxiliary) FirmwareTables()
    {
        var stages = LenovoFanCapture.OwnerReference.Stages;
        var cpu = stages.Select((stage, index) => new FanCurvePoint(stage.CpuTemperatureC, index == 9 ? 5200 : stage.CpuRpm, "RPM")).ToArray();
        var gpu = stages.Select((stage, index) => new FanCurvePoint(stage.GpuTemperatureC, index == 9 ? 5200 : stage.GpuRpm, "RPM")).ToArray();
        var auxiliary = stages.Select(stage => new FanCurvePoint(stage.AuxiliaryCpuTemperatureC ?? 127, 0, "RPM")).ToArray();
        return (cpu, gpu, auxiliary);
    }
}
