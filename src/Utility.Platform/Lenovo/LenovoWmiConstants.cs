using Utility.Core.Models;

namespace Utility.Platform.Lenovo;

/// <summary>Documented Lenovo WMI class names and feature IDs for Legion 83JJ probing and control.</summary>
public static class LenovoWmiConstants
{
    public const string WmiNamespace = @"root\wmi";
    public const string CimNamespace = @"root\cimv2";

    public const string GameZoneDataClass = "LENOVO_GAMEZONE_DATA";
    public const string OtherMethodClass = "LENOVO_OTHER_METHOD";
    public const string CapabilityData00Class = "LENOVO_CAPABILITY_DATA_00";
    public const string CapabilityData01Class = "LENOVO_CAPABILITY_DATA_01";
    public const string DiscreteDataClass = "LENOVO_DISCRETE_DATA";
    public const string FanTableDataClass = "LENOVO_FAN_TABLE_DATA";
    public const string FanMethodClass = "LENOVO_FAN_METHOD";

    public static readonly IReadOnlyDictionary<string, uint> PowerFieldCapabilityIds =
        new Dictionary<string, uint>(StringComparer.Ordinal)
        {
            [nameof(ProfilePowerSettings.CpuShortTermPowerLimitW)] = 0x0101FF00u,
            [nameof(ProfilePowerSettings.CpuLongTermPowerLimitW)] = 0x0102FF00u,
            [nameof(ProfilePowerSettings.CpuThermalControlCircuitC)] = 0x0104FF00u,
            [nameof(ProfilePowerSettings.CpuCrossLoadingPowerLimitW)] = 0x0106FF00u,
            [nameof(ProfilePowerSettings.CpuLongTermPowerLimitTau)] = 0x0107FF00u,
            [nameof(ProfilePowerSettings.GpuDynamicBoostW)] = 0x0201FF00u,
            [nameof(ProfilePowerSettings.GpuConfigurableTgpW)] = 0x0202FF00u,
            [nameof(ProfilePowerSettings.GpuTargetTemperatureLimitC)] = 0x0203FF00u,
            [nameof(ProfilePowerSettings.TotalProcessorPowerTargetAcW)] = 0x0204FF00u,
            [nameof(ProfilePowerSettings.GpuToCpuDynamicBoostW)] = 0x020BFF00u
        };

    // Ordered sequence for deterministic profile writes: CPU limits -> thermal -> GPU limits -> total AC target.
    public static readonly string[] OrderedPowerFieldKeys =
    [
        nameof(ProfilePowerSettings.CpuLongTermPowerLimitW),
        nameof(ProfilePowerSettings.CpuCrossLoadingPowerLimitW),
        nameof(ProfilePowerSettings.CpuLongTermPowerLimitTau),
        nameof(ProfilePowerSettings.CpuShortTermPowerLimitW),
        nameof(ProfilePowerSettings.CpuThermalControlCircuitC),
        nameof(ProfilePowerSettings.GpuConfigurableTgpW),
        nameof(ProfilePowerSettings.GpuDynamicBoostW),
        nameof(ProfilePowerSettings.GpuToCpuDynamicBoostW),
        nameof(ProfilePowerSettings.GpuTargetTemperatureLimitC),
        nameof(ProfilePowerSettings.TotalProcessorPowerTargetAcW)
    ];

    public const uint CapCpuCurrentTemp = 0x05040000u;
    public const uint CapGpuCurrentTemp = 0x05050000u;
    public const uint CapCpuFanSpeed = 0x04030001u;
    public const uint CapGpuFanSpeed = 0x04030002u;
    public const uint CapFanFullSpeed = 0x04020000u;
}
