using Utility.Core.Models;
using Utility.Core.Services;
using Xunit;

namespace Utility.Tests;

public sealed class ProfileSeedAndValidationTests
{
    private readonly ProfileValidationService _validator = new();

    private static IReadOnlyList<FieldHardwareContract> VerifiedContracts() =>
        ProfileSeedFactory.CreateDefaultFieldContracts()
            .Select(c => c with { IsVerifiedOnTarget = true })
            .ToList();

    [Fact]
    public void SeedsMatchOwnerSection3BaselinesExactly()
    {
        var daily = ProfileSeedFactory.CreatePristineReference(ProfileSeedFactory.DailyId);
        var gaming = ProfileSeedFactory.CreatePristineReference(ProfileSeedFactory.GamingId);

        Assert.Equal(55, daily.Power.CpuLongTermPowerLimitW);
        Assert.Equal(45, daily.Power.CpuCrossLoadingPowerLimitW);
        Assert.Equal(56, daily.Power.CpuLongTermPowerLimitTau);
        Assert.Equal(100, daily.Power.CpuShortTermPowerLimitW);
        Assert.Equal(85, daily.Power.CpuThermalControlCircuitC);
        Assert.Equal(20, daily.Power.GpuDynamicBoostW);
        Assert.Equal(0, daily.Power.GpuToCpuDynamicBoostW);
        Assert.Equal(85, daily.Power.GpuConfigurableTgpW);
        Assert.Equal(80, daily.Power.GpuTargetTemperatureLimitC);
        Assert.Equal(45, daily.Power.TotalProcessorPowerTargetAcW);
        Assert.Equal(WindowsPowerMode.BestPerformance, daily.WindowsPowerMode);
        Assert.Equal(FanControlMode.CustomCurve, daily.FanMode);
        Assert.Equal(FanCurveStatus.PendingCapture, daily.FanCurve.Status);

        Assert.Equal(55, gaming.Power.CpuLongTermPowerLimitW);
        Assert.Equal(55, gaming.Power.CpuCrossLoadingPowerLimitW);
        Assert.Equal(56, gaming.Power.CpuLongTermPowerLimitTau);
        Assert.Equal(100, gaming.Power.CpuShortTermPowerLimitW);
        Assert.Equal(90, gaming.Power.CpuThermalControlCircuitC);
        Assert.Equal(0, gaming.Power.GpuDynamicBoostW);
        Assert.Equal(0, gaming.Power.GpuToCpuDynamicBoostW);
        Assert.Equal(115, gaming.Power.GpuConfigurableTgpW);
        Assert.Equal(83, gaming.Power.GpuTargetTemperatureLimitC);
        Assert.Equal(55, gaming.Power.TotalProcessorPowerTargetAcW);
    }

    [Fact]
    public void PreservesValidZeroButRejectsNullMissingField()
    {
        var gaming = ProfileSeedFactory.CreatePristineReference(ProfileSeedFactory.GamingId);
        var contracts = VerifiedContracts();

        var validRes = _validator.ValidatePowerFields(gaming.Power, contracts, isOnAcPower: true);
        Assert.True(validRes.IsValid);
        Assert.Equal(0, validRes.ConvertedBackendValues[nameof(ProfilePowerSettings.GpuDynamicBoostW)]);
        Assert.Equal(0, validRes.ConvertedBackendValues[nameof(ProfilePowerSettings.GpuToCpuDynamicBoostW)]);

        var missing = gaming.Power with { GpuDynamicBoostW = null };
        var invalidRes = _validator.ValidatePowerFields(missing, contracts, isOnAcPower: true);
        Assert.False(invalidRes.IsValid);
        Assert.Contains(nameof(ProfilePowerSettings.GpuDynamicBoostW), invalidRes.MissingFields);
    }

    [Fact]
    public void RejectsUnalignedStepsAndBatteryWhenAcRequired()
    {
        var daily = ProfileSeedFactory.CreatePristineReference(ProfileSeedFactory.DailyId);
        var contracts = VerifiedContracts();

        var badStep = daily.Power with { GpuConfigurableTgpW = 87 }; // Step is 5 from Min 45.
        var stepRes = _validator.ValidatePowerFields(badStep, contracts, isOnAcPower: true);
        Assert.False(stepRes.IsValid);

        var batteryRes = _validator.ValidatePowerFields(daily.Power, contracts, isOnAcPower: false);
        Assert.False(batteryRes.IsValid);
    }

    [Fact]
    public void ConvertsTccOffsetAndAcBaselineTargetCorrectly()
    {
        var tccContract = new FieldHardwareContract(
            nameof(ProfilePowerSettings.CpuThermalControlCircuitC),
            "0x0104FF00", "C", FieldEncodingKind.TccOffsetFromTjMax, 70, 100, 1, 0, true, false, 100);
        Assert.Equal(15, ProfileValidationService.ToBackendValue(85, tccContract));
        Assert.Equal(85, ProfileValidationService.FromBackendValue(15, tccContract));

        var acContract = new FieldHardwareContract(
            nameof(ProfilePowerSettings.TotalProcessorPowerTargetAcW),
            "0x0204FF00", "W", FieldEncodingKind.AcTargetOffsetFromBaseline, 30, 100, 1, 0, true, true, 45);
        Assert.Equal(10, ProfileValidationService.ToBackendValue(55, acContract));
        Assert.Equal(55, ProfileValidationService.FromBackendValue(10, acContract));
    }

    [Fact]
    public void FanCurveValidationBlocksPendingAndMalformedCurves()
    {
        var daily = ProfileSeedFactory.CreatePristineReference(ProfileSeedFactory.DailyId);
        var pendingPreflight = _validator.PreflightProfile(daily, VerifiedContracts(), isOnAcPower: true);
        Assert.False(pendingPreflight.CanApplyFullProfile);
        Assert.True(pendingPreflight.CanApplyPowerFieldsOnly);

        var nonMonotonic = new FanCurveProfile(
            FanCurveStatus.Captured,
            "Test",
            [new FanChannelCurve(0, "CPU", "CPU", [new(50, 2000, "RPM"), new(45, 2500, "RPM")], 30, 100, 1, 0, 5600, 100)]);
        var fanRes = _validator.ValidateFanCurve(nonMonotonic);
        Assert.False(fanRes.IsValid);
    }

    [Fact]
    public void LiveSnapshotDoesNotClaimDailyWhenGenericCustomDiffers()
    {
        var daily = ProfileSeedFactory.CreatePristineReference(ProfileSeedFactory.DailyId);
        var live = new LiveThermalSnapshot(
            ThermalMode.Custom,
            WindowsPowerMode.BestPerformance,
            "Balanced",
            true,
            daily.Power with { GpuConfigurableTgpW = 100 },
            null,
            true,
            "Live",
            DateTimeOffset.UtcNow);

        Assert.False(ProfileValidationService.DoesLiveMatchProfile(live, daily, VerifiedContracts()));
    }
}
