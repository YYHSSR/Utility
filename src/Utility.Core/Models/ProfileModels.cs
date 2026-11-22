namespace Utility.Core.Models;

/// <summary>Firmware thermal mode exposed by Lenovo GameZone WMI.</summary>
public enum ThermalMode
{
    Unknown = 0,
    Quiet = 1,
    Balanced = 2,
    Performance = 3,
    Custom = 255
}

/// <summary>Windows power overlay mode mapped independently from Lenovo thermal mode.</summary>
public enum WindowsPowerMode
{
    Unknown = 0,
    BestPowerEfficiency = 1,
    Balanced = 2,
    BestPerformance = 3
}

/// <summary>Fan control mode selected in a performance profile.</summary>
public enum FanControlMode
{
    Auto = 0,
    CustomCurve = 1
}

/// <summary>Capture and verification status of a custom fan curve.</summary>
public enum FanCurveStatus
{
    PendingCapture = 0,
    Captured = 1,
    Verified = 2
}

/// <summary>Encoding scheme between displayed UI units and firmware WMI values.</summary>
public enum FieldEncodingKind
{
    DirectValue = 0,
    TccOffsetFromTjMax = 1,
    AcTargetOffsetFromBaseline = 2
}

/// <summary>
/// Ten distinct power and thermal fields observed in the owner's Legion Zone baselines.
/// Nullable integers ensure missing values are never conflated with valid zero values.
/// </summary>
public sealed record ProfilePowerSettings
{
    public int? CpuLongTermPowerLimitW { get; init; }
    public int? CpuCrossLoadingPowerLimitW { get; init; }
    public int? CpuLongTermPowerLimitTau { get; init; }
    public int? CpuShortTermPowerLimitW { get; init; }
    public int? CpuThermalControlCircuitC { get; init; }
    public int? GpuDynamicBoostW { get; init; }
    public int? GpuToCpuDynamicBoostW { get; init; }
    public int? GpuConfigurableTgpW { get; init; }
    public int? GpuTargetTemperatureLimitC { get; init; }
    public int? TotalProcessorPowerTargetAcW { get; init; }

    public IReadOnlyDictionary<string, int?> AsFieldMap() => new Dictionary<string, int?>(StringComparer.Ordinal)
    {
        [nameof(CpuLongTermPowerLimitW)] = CpuLongTermPowerLimitW,
        [nameof(CpuCrossLoadingPowerLimitW)] = CpuCrossLoadingPowerLimitW,
        [nameof(CpuLongTermPowerLimitTau)] = CpuLongTermPowerLimitTau,
        [nameof(CpuShortTermPowerLimitW)] = CpuShortTermPowerLimitW,
        [nameof(CpuThermalControlCircuitC)] = CpuThermalControlCircuitC,
        [nameof(GpuDynamicBoostW)] = GpuDynamicBoostW,
        [nameof(GpuToCpuDynamicBoostW)] = GpuToCpuDynamicBoostW,
        [nameof(GpuConfigurableTgpW)] = GpuConfigurableTgpW,
        [nameof(GpuTargetTemperatureLimitC)] = GpuTargetTemperatureLimitC,
        [nameof(TotalProcessorPowerTargetAcW)] = TotalProcessorPowerTargetAcW
    };
}

/// <summary>Single temperature-to-fan-speed control point.</summary>
public sealed record FanCurvePoint(
    int TemperatureC,
    int FanSpeedValue,
    string SpeedUnit);

/// <summary>Single firmware fan channel curve definition and limits.</summary>
public sealed record FanChannelCurve(
    int ChannelId,
    string ChannelName,
    string SensorName,
    IReadOnlyList<FanCurvePoint> Points,
    int MinTempC,
    int MaxTempC,
    int TempStep,
    int MinSpeed,
    int MaxSpeed,
    int SpeedStep,
    string SpeedUnit = "RPM");

/// <summary>All fan channels and provenance for a profile's custom fan curve.</summary>
public sealed record FanCurveProfile(
    FanCurveStatus Status,
    string SourceDescription,
    IReadOnlyList<FanChannelCurve> Channels);

/// <summary>Editable performance profile preserving owner baselines and fan policy.</summary>
public sealed record PerformanceProfile(
    string Id,
    string Name,
    ProfilePowerSettings Power,
    WindowsPowerMode WindowsPowerMode,
    FanControlMode FanMode,
    FanCurveProfile FanCurve,
    DateTimeOffset LastModifiedUtc);

/// <summary>Verified hardware field contract discovered or configured for a capability ID.</summary>
public sealed record FieldHardwareContract(
    string FieldKey,
    string WmiCapabilityIdHex,
    string DisplayUnit,
    FieldEncodingKind Encoding,
    int MinValue,
    int MaxValue,
    int Step,
    int? FirmwareDefault,
    bool IsVerifiedOnTarget,
    bool RequiresAcPower,
    int ConversionReference = 0,
    string Notes = "");

/// <summary>Live verified hardware thermal/power state read from Windows and Lenovo interfaces.</summary>
public sealed record LiveThermalSnapshot(
    ThermalMode ThermalMode,
    WindowsPowerMode WindowsPowerMode,
    string ActivePowerSchemeName,
    bool IsOnAcPower,
    ProfilePowerSettings? LivePowerValues,
    string? MatchedProfileId,
    bool IsVerifiedLiveRead,
    string StatusNote,
    DateTimeOffset CapturedAtUtc);

/// <summary>Provides exact owner baseline seeds from Specification Section 3.</summary>
public static class ProfileSeedFactory
{
    public const string DailyId = "daily";
    public const string GamingId = "gaming";

    public static IReadOnlyList<PerformanceProfile> CreateEditableSeeds() =>
    [
        CreatePristineReference(DailyId),
        CreatePristineReference(GamingId)
    ];

    public static PerformanceProfile CreatePristineReference(string profileId)
    {
        var timestamp = new DateTimeOffset(2026, 11, 22, 0, 0, 0, TimeSpan.Zero);
        return profileId switch
        {
            DailyId => new PerformanceProfile(
                Id: DailyId,
                Name: "Daily",
                Power: new ProfilePowerSettings
                {
                    CpuLongTermPowerLimitW = 55,
                    CpuCrossLoadingPowerLimitW = 45,
                    CpuLongTermPowerLimitTau = 56,
                    CpuShortTermPowerLimitW = 100,
                    CpuThermalControlCircuitC = 85,
                    GpuDynamicBoostW = 20,
                    GpuToCpuDynamicBoostW = 0,
                    GpuConfigurableTgpW = 85,
                    GpuTargetTemperatureLimitC = 80,
                    TotalProcessorPowerTargetAcW = 45
                },
                WindowsPowerMode: WindowsPowerMode.BestPerformance,
                FanMode: FanControlMode.CustomCurve,
                FanCurve: new FanCurveProfile(
                    FanCurveStatus.PendingCapture,
                    "Pending capture from Legion Zone custom profile editor",
                    []),
                LastModifiedUtc: timestamp),

            GamingId => new PerformanceProfile(
                Id: GamingId,
                Name: "Gaming",
                Power: new ProfilePowerSettings
                {
                    CpuLongTermPowerLimitW = 55,
                    CpuCrossLoadingPowerLimitW = 55,
                    CpuLongTermPowerLimitTau = 56,
                    CpuShortTermPowerLimitW = 100,
                    CpuThermalControlCircuitC = 90,
                    GpuDynamicBoostW = 0,
                    GpuToCpuDynamicBoostW = 0,
                    GpuConfigurableTgpW = 115,
                    GpuTargetTemperatureLimitC = 83,
                    TotalProcessorPowerTargetAcW = 55
                },
                WindowsPowerMode: WindowsPowerMode.BestPerformance,
                FanMode: FanControlMode.CustomCurve,
                FanCurve: new FanCurveProfile(
                    FanCurveStatus.PendingCapture,
                    "Pending capture from Legion Zone custom profile editor",
                    []),
                LastModifiedUtc: timestamp),

            _ => throw new ArgumentOutOfRangeException(nameof(profileId), profileId, "Unknown baseline profile id.")
        };
    }

    /// <summary>
    /// Default target field contracts. IsVerifiedOnTarget remains false until live WMI capability
    /// probing confirms min/max/step and encoding on the target 83JJ BIOS.
    /// </summary>
    public static IReadOnlyList<FieldHardwareContract> CreateDefaultFieldContracts() =>
    [
        new(nameof(ProfilePowerSettings.CpuLongTermPowerLimitW), "0x0102FF00", "W", FieldEncodingKind.DirectValue, 15, 140, 1, null, false, true, 0, "PL1 sustained power limit"),
        new(nameof(ProfilePowerSettings.CpuCrossLoadingPowerLimitW), "0x0106FF00", "W", FieldEncodingKind.DirectValue, 15, 140, 1, null, false, true, 0, "Cross-loading PL1 limit"),
        new(nameof(ProfilePowerSettings.CpuLongTermPowerLimitTau), "0x0107FF00", "s", FieldEncodingKind.DirectValue, 1, 128, 1, null, false, true, 0, "PL1 Tau time window"),
        new(nameof(ProfilePowerSettings.CpuShortTermPowerLimitW), "0x0101FF00", "W", FieldEncodingKind.DirectValue, 25, 175, 1, null, false, true, 0, "PL2 short-term turbo power limit"),
        new(nameof(ProfilePowerSettings.CpuThermalControlCircuitC), "0x0104FF00", "C", FieldEncodingKind.DirectValue, 70, 105, 1, null, false, false, 100, "Verify direct C vs TjMax offset against firmware"),
        new(nameof(ProfilePowerSettings.GpuDynamicBoostW), "0x0201FF00", "W", FieldEncodingKind.DirectValue, 0, 25, 1, null, false, true, 0, "NVIDIA Dynamic Boost"),
        new(nameof(ProfilePowerSettings.GpuToCpuDynamicBoostW), "0x020BFF00", "W", FieldEncodingKind.DirectValue, 0, 25, 1, null, false, true, 0, "GPU to CPU Dynamic Boost"),
        new(nameof(ProfilePowerSettings.GpuConfigurableTgpW), "0x0202FF00", "W", FieldEncodingKind.DirectValue, 45, 140, 5, null, false, true, 0, "Configurable TGP"),
        new(nameof(ProfilePowerSettings.GpuTargetTemperatureLimitC), "0x0203FF00", "C", FieldEncodingKind.DirectValue, 75, 87, 1, null, false, false, 0, "GPU target temperature"),
        new(nameof(ProfilePowerSettings.TotalProcessorPowerTargetAcW), "0x0204FF00", "W", FieldEncodingKind.DirectValue, 25, 140, 1, null, false, true, 0, "Verify direct watts vs baseline offset against firmware")
    ];
}
