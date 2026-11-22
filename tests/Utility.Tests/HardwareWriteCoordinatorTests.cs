using Utility.Core.Models;
using Utility.Core.Services;
using Xunit;

namespace Utility.Tests;

public sealed class HardwareWriteCoordinatorTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"UtilityCoord_{Guid.NewGuid():N}");

    private static IReadOnlyList<FieldHardwareContract> VerifiedContracts() =>
        ProfileSeedFactory.CreateDefaultFieldContracts()
            .Select(c => c with { IsVerifiedOnTarget = true })
            .ToList();

    [Fact]
    public async Task DetectsSameStateNoOpWithoutWriting()
    {
        var sim = new SimulatedLenovoHardwareProvider
        {
            CurrentThermalMode = ThermalMode.Custom,
            CurrentWindowsPowerMode = WindowsPowerMode.BestPerformance,
            CurrentPowerValues = ProfileSeedFactory.CreatePristineReference(ProfileSeedFactory.DailyId).Power
        };
        var dailyWithPendingCurve = ProfileSeedFactory.CreatePristineReference(ProfileSeedFactory.DailyId);
        var store = new JsonStateStore(_tempDir);
        var coord = new HardwareWriteCoordinator(sim, store, new ProfileValidationService());

        var report = await coord.ApplyPowerFieldsAsync(dailyWithPendingCurve, VerifiedContracts());
        Assert.Equal(OperationStatus.NoOpAlreadyMatches, report.Status);
        Assert.Equal(0, sim.WriteCallsCount);
    }

    [Fact]
    public async Task RollsBackOnMidOperationFailureAndReReadsBaseline()
    {
        var daily = ProfileSeedFactory.CreatePristineReference(ProfileSeedFactory.DailyId);
        var gaming = ProfileSeedFactory.CreatePristineReference(ProfileSeedFactory.GamingId);
        var sim = new SimulatedLenovoHardwareProvider
        {
            CurrentThermalMode = ThermalMode.Balanced,
            CurrentPowerValues = daily.Power,
            FailNextPowerWrite = true
        };
        var store = new JsonStateStore(_tempDir);
        var coord = new HardwareWriteCoordinator(sim, store, new ProfileValidationService());

        var report = await coord.ApplyPowerFieldsAsync(gaming, VerifiedContracts());
        Assert.Equal(OperationStatus.RolledBack, report.Status);
        Assert.Equal(ThermalMode.Balanced, sim.CurrentThermalMode);
        Assert.Equal(85, sim.CurrentPowerValues.GpuConfigurableTgpW);
        Assert.Null(await coord.GetInterruptedOperationAsync());
    }

    [Fact]
    public async Task CancelledElevationLeavesHardwareAndProfileStateIntact()
    {
        var daily = ProfileSeedFactory.CreatePristineReference(ProfileSeedFactory.DailyId);
        var gaming = ProfileSeedFactory.CreatePristineReference(ProfileSeedFactory.GamingId);
        var sim = new SimulatedLenovoHardwareProvider
        {
            CurrentThermalMode = ThermalMode.Balanced,
            CurrentPowerValues = daily.Power,
            SimulateUserCancelled = true
        };
        var store = new JsonStateStore(_tempDir);
        var coord = new HardwareWriteCoordinator(sim, store, new ProfileValidationService());

        var report = await coord.ApplyPowerFieldsAsync(gaming, VerifiedContracts());
        Assert.Equal(OperationStatus.CancelledByUser, report.Status);
        Assert.Equal(ThermalMode.Balanced, sim.CurrentThermalMode);
        Assert.Equal(0, sim.WriteCallsCount);
    }

    [Fact]
    public async Task UnverifiedOwnerPowerSwitchRequiresAcknowledgmentBeforeAnotherSwitch()
    {
        var store = new JsonStateStore(_tempDir);
        var hardware = new SimulatedLenovoHardwareProvider
        {
            OwnerPowerResult = OperationResult<LiveThermalSnapshot>.Fail("RESTORE_UNVERIFIED", "Failed read-back")
        };
        var coordinator = new HardwareWriteCoordinator(hardware, store, new ProfileValidationService());

        var first = await coordinator.ApplyOwnerPowerPresetAsync(ProfileSeedFactory.GamingId);
        var pending = Assert.IsType<OperationJournalEntry>(await coordinator.GetInterruptedOperationAsync());
        var second = await coordinator.ApplyOwnerPowerPresetAsync(ProfileSeedFactory.DailyId);
        Assert.Equal("RESTORE_UNVERIFIED", first.ErrorCode);
        Assert.Equal("RECOVERY_PENDING", second.ErrorCode);
        Assert.Equal(ProfileSeedFactory.GamingId, pending.TargetDescription);
    }

    [Fact]
    public async Task UnverifiedCoolingRestorationBlocksProfileWritesUntilAcknowledged()
    {
        var store = new JsonStateStore(_tempDir);
        var hardware = new SimulatedLenovoHardwareProvider
        {
            CoolingResult = OperationResult<CoolingCycleResult>.Fail("RESTORE_UNVERIFIED", "Fan state could not be read.")
        };
        var coordinator = new HardwareWriteCoordinator(hardware, store, new ProfileValidationService());

        var cooling = await coordinator.RunCoolingCycleAsync(CancellationToken.None);
        var pending = Assert.IsType<OperationJournalEntry>(await coordinator.GetInterruptedOperationAsync());
        var profile = await coordinator.ApplyOwnerPowerPresetAsync(ProfileSeedFactory.DailyId);

        Assert.Equal("RESTORE_UNVERIFIED", cooling.ErrorCode);
        Assert.Equal("CoolingCycle", pending.OperationType);
        Assert.Equal("RECOVERY_PENDING", profile.ErrorCode);
    }

    [Fact]
    public async Task DetectsAndDismissesCrashInterruptedOperationOnStartup()
    {
        var store = new JsonStateStore(_tempDir);
        await store.SaveJournalAsync(new OperationJournalState
        {
            ActiveInProgress = new OperationJournalEntry
            {
                OperationId = "op-crash-1",
                OperationType = "ApplyPowerFields",
                TargetDescription = "Gaming",
                Status = OperationStatus.InProgress,
                StartedUtc = DateTimeOffset.UtcNow
            }
        });

        var coord = new HardwareWriteCoordinator(new SimulatedLenovoHardwareProvider(), store, new ProfileValidationService());
        var interrupted = await coord.GetInterruptedOperationAsync();
        Assert.NotNull(interrupted);
        Assert.Equal("op-crash-1", interrupted.OperationId);

        await coord.DismissInterruptedOperationAsync("op-crash-1", "Owner confirmed recovery.");
        Assert.Null(await coord.GetInterruptedOperationAsync());
        Assert.Equal(OperationStatus.Acknowledged, Assert.Single((await store.LoadJournalAsync()).History).Status);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
