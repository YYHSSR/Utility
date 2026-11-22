using Utility.Core.Models;
using Utility.Core.Services;
using Utility.Platform.Native;
using Utility.Platform.Providers;
using Xunit;

namespace Utility.Tests;

public sealed class CapabilityAndTelemetryTests
{
    [Fact]
    public async Task ProductionProvidersRejectUnverifiedHardwareWrites()
    {
        var battery = new WindowsBatteryProvider();
        using var telemetry = new WindowsTelemetryProvider();
        var hardware = new LenovoHardwareControlProvider(battery, telemetry);

        Assert.Equal("WRITE_NOT_VALIDATED", (await hardware.SetThermalModeAsync(ThermalMode.Performance)).ErrorCode);
        Assert.Equal("WRITE_NOT_VALIDATED", (await hardware.RestoreDefaultFanCurveAsync()).ErrorCode);
        Assert.Equal("WRITE_NOT_VALIDATED", (await hardware.ApplyPowerFieldsAsync(new Dictionary<string, int>(), [])).ErrorCode);
    }

    [Fact]
    public void EnergyDrvBitmaskDecodesObserved83JJAndConservationStates()
    {
        // Observed on target 83JJ via IOCTL 0x831020F8(0xFF) in standard-user mode.
        Assert.Equal(BatteryChargeMode.Normal, EnergyDrvInterop.DecodeBatteryChargeMode(0x10860A00u));
        Assert.Equal(BatteryChargeMode.Conservation, EnergyDrvInterop.DecodeBatteryChargeMode(0x10860820u));

        // Bit 29 set without active normal charging bit 17 -> Conservation.
        Assert.Equal(BatteryChargeMode.Conservation, EnergyDrvInterop.DecodeBatteryChargeMode(1u << 29));

        // Bit 17 + Bit 26 -> RapidCharge.
        Assert.Equal(BatteryChargeMode.RapidCharge, EnergyDrvInterop.DecodeBatteryChargeMode((1u << 17) | (1u << 26)));
    }

    [Fact]
    public async Task IntegratedModeSkipsNvidiaEvenOnManualRefreshAndResumesSystemCounters()
    {
        using var provider = new WindowsTelemetryProvider();
        for (var i = 0; i < 2; i++)
        {
            var sample = await provider.ReadSnapshotAsync(allowWakeDgpu: true, pauseDgpu: true);
            Assert.Equal(SensorStatus.PausedInIntegratedMode, sample.GpuUtilization.Status);
            Assert.Null(sample.GpuTemperature.Value);
            Assert.Equal(SensorStatus.Valid, sample.MemoryTotalGb.Status);
            provider.ReleaseIdleResources();
        }
    }

    [Fact]
    public void SensorReadingDistinguishesZeroFromUnavailableAndPaused()
    {
        var now = DateTimeOffset.UtcNow;
        var zeroFan = new SensorReading<int>(0, "RPM", SensorStatus.Valid, "LENOVO_GAMEZONE_DATA", now);
        var unavailFan = new SensorReading<int>(null, "RPM", SensorStatus.Unavailable, "LENOVO_GAMEZONE_DATA", now);
        var pausedGpu = new SensorReading<double>(null, "%", SensorStatus.PausedOnBattery, "nvidia-smi", now);

        Assert.Equal(0, zeroFan.Value);
        Assert.Equal(SensorStatus.Valid, zeroFan.Status);
        Assert.Null(unavailFan.Value);
        Assert.Equal(SensorStatus.Unavailable, unavailFan.Status);
        Assert.Equal(SensorStatus.PausedOnBattery, pausedGpu.Status);
    }

    [Fact]
    public void CapabilityAvailabilityEnumCoversAllRequiredFailureStates()
    {
        var values = Enum.GetValues<CapabilityAvailability>();
        Assert.Contains(CapabilityAvailability.Available, values);
        Assert.Contains(CapabilityAvailability.UnsupportedMachine, values);
        Assert.Contains(CapabilityAvailability.UnavailableInterface, values);
        Assert.Contains(CapabilityAvailability.InsufficientPrivilege, values);
        Assert.Contains(CapabilityAvailability.TransientFailure, values);
        Assert.Contains(CapabilityAvailability.RebootPending, values);
    }
}
