namespace Utility.Core.Models;

/// <summary>Lenovo battery charge policy decoded from EnergyDrv IOCTL 0x831020F8.</summary>
public enum BatteryChargeMode
{
    Unknown = 0,
    Normal = 1,
    Conservation = 2,
    RapidCharge = 3
}

/// <summary>Windows battery charging status derived from native Win32 power and battery IOCTLs.</summary>
public enum BatteryChargeActivity
{
    Unknown = 0,
    Charging = 1,
    Discharging = 2,
    IdleOnAc = 3,
    Critical = 4
}

/// <summary>Combined Windows battery telemetry and Lenovo conservation policy state.</summary>
public sealed record BatteryStatusSnapshot(
    bool IsAcOnline,
    int? ChargePercent,
    BatteryChargeActivity Activity,
    int? DesignCapacityMwh,
    int? FullChargeCapacityMwh,
    int? RemainingCapacityMwh,
    double? HealthPercent,
    int? CycleCount,
    int? VoltageMv,
    int? ChargeRateMw,
    BatteryChargeMode LenovoChargeMode,
    bool IsConservationSupported,
    bool IsConservationWriteSupported,
    uint? RawEnergyDrvState,
    string ProviderNote,
    DateTimeOffset CapturedAtUtc);
