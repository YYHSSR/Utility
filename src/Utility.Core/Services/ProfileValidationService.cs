using Utility.Core.Models;

namespace Utility.Core.Services;

public sealed record PowerValidationResult(
    bool IsValid,
    bool HasUnverifiedContracts,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> MissingFields,
    IReadOnlyDictionary<string, int> ConvertedBackendValues);

public sealed record FanValidationResult(
    bool IsValid,
    bool IsPendingCapture,
    IReadOnlyList<string> Errors);

public sealed record ProfileApplyPreflight(
    bool CanApplyFullProfile,
    bool CanApplyPowerFieldsOnly,
    IReadOnlyList<string> BlockingReasons,
    PowerValidationResult PowerValidation,
    FanValidationResult FanValidation);

/// <summary>
/// Validates profile power limits, unit conversions, and custom fan curves prior to any hardware write.
/// </summary>
public sealed class ProfileValidationService
{
    public static int ToBackendValue(int displayValue, FieldHardwareContract contract) =>
        contract.Encoding switch
        {
            FieldEncodingKind.DirectValue => displayValue,
            FieldEncodingKind.TccOffsetFromTjMax => contract.ConversionReference - displayValue,
            FieldEncodingKind.AcTargetOffsetFromBaseline => displayValue - contract.ConversionReference,
            _ => throw new ArgumentOutOfRangeException(nameof(contract), "Unsupported field encoding.")
        };

    public static int FromBackendValue(int backendValue, FieldHardwareContract contract) =>
        contract.Encoding switch
        {
            FieldEncodingKind.DirectValue => backendValue,
            FieldEncodingKind.TccOffsetFromTjMax => contract.ConversionReference - backendValue,
            FieldEncodingKind.AcTargetOffsetFromBaseline => contract.ConversionReference + backendValue,
            _ => throw new ArgumentOutOfRangeException(nameof(contract), "Unsupported field encoding.")
        };

    public PowerValidationResult ValidatePowerFields(
        ProfilePowerSettings power,
        IReadOnlyList<FieldHardwareContract> contracts,
        bool isOnAcPower,
        bool requireVerifiedContracts = true)
    {
        var errors = new List<string>();
        var missing = new List<string>();
        var backendValues = new Dictionary<string, int>(StringComparer.Ordinal);
        var hasUnverified = false;
        var fieldMap = power.AsFieldMap();

        foreach (var contract in contracts)
        {
            if (!fieldMap.TryGetValue(contract.FieldKey, out var value) || value is null)
            {
                missing.Add(contract.FieldKey);
                errors.Add($"Field '{contract.FieldKey}' is missing.");
                continue;
            }

            // Preserve zero as a legitimate value whenever firmware bounds include 0.
            var numeric = value.Value;
            if (numeric < contract.MinValue || numeric > contract.MaxValue)
            {
                errors.Add($"Field '{contract.FieldKey}' value {numeric} {contract.DisplayUnit} is outside [{contract.MinValue}, {contract.MaxValue}].");
                continue;
            }

            if (contract.Step > 1 && (numeric - contract.MinValue) % contract.Step != 0)
            {
                errors.Add($"Field '{contract.FieldKey}' value {numeric} does not align with step size {contract.Step}.");
                continue;
            }

            if (contract.RequiresAcPower && !isOnAcPower)
            {
                errors.Add($"Field '{contract.FieldKey}' requires AC power adapter connection.");
                continue;
            }

            if (!contract.IsVerifiedOnTarget)
            {
                hasUnverified = true;
                if (requireVerifiedContracts)
                {
                    errors.Add($"Field '{contract.FieldKey}' ({contract.WmiCapabilityIdHex}) has not been verified on target hardware.");
                    continue;
                }
            }

            backendValues[contract.FieldKey] = ToBackendValue(numeric, contract);
        }

        return new PowerValidationResult(
            IsValid: errors.Count == 0,
            HasUnverifiedContracts: hasUnverified,
            Errors: errors,
            MissingFields: missing,
            ConvertedBackendValues: backendValues);
    }

    public FanValidationResult ValidateFanCurve(FanCurveProfile fanCurve)
    {
        var errors = new List<string>();
        if (fanCurve.Status == FanCurveStatus.PendingCapture)
        {
            errors.Add("Custom fan curve is pending capture from a verified source.");
            return new FanValidationResult(false, true, errors);
        }

        if (fanCurve.Channels.Count == 0)
        {
            errors.Add("Fan curve contains no fan channels.");
            return new FanValidationResult(false, false, errors);
        }

        foreach (var channel in fanCurve.Channels)
        {
            if (channel.Points.Count < 2)
            {
                errors.Add($"Channel '{channel.ChannelName}' must define at least 2 points without extrapolation.");
                continue;
            }

            int? prevTemp = null;
            int? prevSpeed = null;

            foreach (var pt in channel.Points)
            {
                if (pt.TemperatureC < channel.MinTempC || pt.TemperatureC > channel.MaxTempC)
                {
                    errors.Add($"Channel '{channel.ChannelName}' temperature {pt.TemperatureC} C is outside [{channel.MinTempC}, {channel.MaxTempC}].");
                }

                if (channel.TempStep > 1 && (pt.TemperatureC - channel.MinTempC) % channel.TempStep != 0)
                {
                    errors.Add($"Channel '{channel.ChannelName}' temperature {pt.TemperatureC} C violates step {channel.TempStep}.");
                }

                if (pt.FanSpeedValue < channel.MinSpeed || pt.FanSpeedValue > channel.MaxSpeed)
                {
                    errors.Add($"Channel '{channel.ChannelName}' speed {pt.FanSpeedValue} {channel.SpeedUnit} is outside [{channel.MinSpeed}, {channel.MaxSpeed}].");
                }

                if (channel.SpeedStep > 1 && (pt.FanSpeedValue - channel.MinSpeed) % channel.SpeedStep != 0)
                {
                    errors.Add($"Channel '{channel.ChannelName}' speed {pt.FanSpeedValue} violates step {channel.SpeedStep}.");
                }

                if (prevTemp is not null && pt.TemperatureC <= prevTemp.Value)
                {
                    errors.Add($"Channel '{channel.ChannelName}' temperatures must be strictly increasing ({prevTemp.Value} -> {pt.TemperatureC}).");
                }

                if (prevSpeed is not null && pt.FanSpeedValue < prevSpeed.Value)
                {
                    errors.Add($"Channel '{channel.ChannelName}' fan speeds must be non-decreasing ({prevSpeed.Value} -> {pt.FanSpeedValue}).");
                }

                prevTemp = pt.TemperatureC;
                prevSpeed = pt.FanSpeedValue;
            }
        }

        return new FanValidationResult(errors.Count == 0, false, errors);
    }

    public ProfileApplyPreflight PreflightProfile(
        PerformanceProfile profile,
        IReadOnlyList<FieldHardwareContract> contracts,
        bool isOnAcPower)
    {
        var powerValidation = ValidatePowerFields(profile.Power, contracts, isOnAcPower, requireVerifiedContracts: true);
        var fanValidation = profile.FanMode == FanControlMode.CustomCurve
            ? ValidateFanCurve(profile.FanCurve)
            : new FanValidationResult(true, false, []);

        var blocking = new List<string>();
        blocking.AddRange(powerValidation.Errors);
        blocking.AddRange(fanValidation.Errors);

        var canPowerOnly = powerValidation.IsValid;
        var canFull = powerValidation.IsValid && fanValidation.IsValid;

        return new ProfileApplyPreflight(
            CanApplyFullProfile: canFull,
            CanApplyPowerFieldsOnly: canPowerOnly,
            BlockingReasons: blocking,
            PowerValidation: powerValidation,
            FanValidation: fanValidation);
    }

    public static bool DoesLiveMatchProfile(
        LiveThermalSnapshot live,
        PerformanceProfile profile,
        IReadOnlyList<FieldHardwareContract> contracts)
    {
        if (!live.IsVerifiedLiveRead || live.ThermalMode != ThermalMode.Custom || live.LivePowerValues is null)
        {
            return false;
        }

        var liveMap = live.LivePowerValues.AsFieldMap();
        var profileMap = profile.Power.AsFieldMap();
        foreach (var contract in contracts)
        {
            if (!profileMap.TryGetValue(contract.FieldKey, out var expected) || expected is null)
            {
                return false;
            }

            if (!liveMap.TryGetValue(contract.FieldKey, out var actual) || actual is null)
            {
                return false;
            }

            if (Math.Abs(actual.Value - expected.Value) > Math.Max(0, contract.Step - 1))
            {
                return false;
            }
        }

        return profile.FanMode != FanControlMode.CustomCurve || profile.FanCurve.Status == FanCurveStatus.Verified;
    }
}
