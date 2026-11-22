using Utility.Core.Models;
using Utility.Core.Services;
using Utility.Platform.Lenovo;
using Utility.Platform.Native;

namespace Utility.Platform.Providers;

/// <summary>Reads Windows battery telemetry and the observed Lenovo charge mode.</summary>
public sealed class WindowsBatteryProvider : IBatteryProvider
{
    private readonly Lazy<bool> _canChangePolicy = new(() => new LenovoWmiProvider().IsKnown83JJFirmware());

    public Task<BatteryStatusSnapshot> ReadStatusAsync(CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var now = DateTimeOffset.UtcNow;
            var native = Win32BatteryInterop.ReadNativeBatteryDetails();
            double? health = null;
            if (native.DesignCapacityMwh is > 0 && native.FullChargeCapacityMwh is > 0)
            {
                health = Math.Round(native.FullChargeCapacityMwh.Value * 100.0 / native.DesignCapacityMwh.Value, 2);
            }

            var energy = EnergyDrvInterop.ReadChargeMode();
            var note = $"{native.Note} | {energy.Note}";

            return new BatteryStatusSnapshot(
                IsAcOnline: native.IsAcOnline,
                ChargePercent: native.ChargePercent,
                Activity: native.Activity,
                DesignCapacityMwh: native.DesignCapacityMwh,
                FullChargeCapacityMwh: native.FullChargeCapacityMwh,
                RemainingCapacityMwh: native.RemainingCapacityMwh,
                HealthPercent: health,
                CycleCount: native.CycleCount,
                VoltageMv: native.VoltageMv,
                ChargeRateMw: native.ChargeRateMw,
                LenovoChargeMode: energy.Success ? energy.Mode : BatteryChargeMode.Unknown,
                IsConservationSupported: energy.Success,
                IsConservationWriteSupported: energy.Success && _canChangePolicy.Value,
                RawEnergyDrvState: energy.Success ? energy.RawValue : null,
                ProviderNote: note,
                CapturedAtUtc: now);
        }, ct);
    }

    public Task<FeatureCapability> ProbeCapabilityAsync(CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var now = DateTimeOffset.UtcNow;
            var native = Win32BatteryInterop.ReadNativeBatteryDetails();
            var energy = EnergyDrvInterop.ReadChargeMode();

            var details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Win32BatterySource"] = native.Note,
                ["EnergyDrvStatus"] = energy.Note
            };
            if (energy.Success)
            {
                details["EnergyDrvRawHex"] = $"0x{energy.RawValue:X8}";
                details["DecodedMode"] = energy.Mode.ToString();
            }

            if (energy.Success)
            {
                return new FeatureCapability(
                    FeatureId.BatteryConservation,
                    ReadAvailable: true,
                    WriteAvailable: _canChangePolicy.Value,
                    Status: CapabilityAvailability.Available,
                    Reason: "Win32 battery telemetry and Lenovo charging policy are available.",
                    Provider: "Win32 IOCTL_BATTERY_QUERY_INFORMATION + EnergyDrv (0x831020F8)",
                    LastCheckedUtc: now,
                    Details: details);
            }

            var status = energy.Win32Error == 5
                ? CapabilityAvailability.InsufficientPrivilege
                : CapabilityAvailability.UnavailableInterface;

            return new FeatureCapability(
                FeatureId.BatteryConservation,
                ReadAvailable: native.ChargePercent is not null,
                WriteAvailable: false,
                Status: status,
                Reason: $"Basic Windows battery telemetry available; Lenovo EnergyDrv unavailable ({energy.Note}).",
                Provider: "Win32 IOCTL_BATTERY_QUERY_INFORMATION",
                LastCheckedUtc: now,
                Details: details);
        }, ct);
    }

    public Task<OperationResult<BatteryChargeMode>> SetConservationModeAsync(bool enabled, CancellationToken ct = default) =>
        Task.Run(() => _canChangePolicy.Value
            ? LenovoWmiProvider.Run83JJWrite(() => EnergyDrvInterop.SetConservationMode83JJ(enabled))
            : OperationResult<BatteryChargeMode>.Fail("BATTERY_UNSUPPORTED", "Charging control requires Lenovo 83JJ / PTCN18WW."), ct);
}
