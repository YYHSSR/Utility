using Utility.Core.Models;
using Utility.Platform.Native;
using Xunit;

namespace Utility.Tests;

public sealed class CapabilityAndTelemetryTests
{
    [Fact]
    public void EnergyDrvBitmaskDecodesObserved83JJAndConservationStates()
    {
        // Observed on target 83JJ via IOCTL 0x831020F8(0xFF) in standard-user mode.
        Assert.Equal(BatteryChargeMode.Normal, EnergyDrvInterop.DecodeBatteryChargeMode(0x10860A00u));

        // Bit 29 set without active normal charging bit 17 -> Conservation.
        Assert.Equal(BatteryChargeMode.Conservation, EnergyDrvInterop.DecodeBatteryChargeMode(1u << 29));

        // Bit 17 + Bit 26 -> RapidCharge.
        Assert.Equal(BatteryChargeMode.RapidCharge, EnergyDrvInterop.DecodeBatteryChargeMode((1u << 17) | (1u << 26)));
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
