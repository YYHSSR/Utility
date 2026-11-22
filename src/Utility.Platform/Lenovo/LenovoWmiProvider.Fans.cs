using System.Management;
using Utility.Core.Models;
using Utility.Core.Services;
using Utility.Platform.Native;

namespace Utility.Platform.Lenovo;

public sealed partial class LenovoWmiProvider
{
    public OperationResult<FanCurveProfile> SetFanSpeedLevelsDirect(int[]? levels)
    {
        if (!LenovoFanCapture.AreWritableLevels(levels))
            return OperationResult<FanCurveProfile>.Fail("FAN_INVALID_LEVELS", "Expected ten nondecreasing levels from 1 to 10.");
        if (!IsKnown83JJFirmware())
            return OperationResult<FanCurveProfile>.Fail("FAN_UNSUPPORTED", "Fan speed levels require Lenovo 83JJ / PTCN18WW.");

        return Run83JJWrite(() => Set83JJFanSpeedLevels(levels!));
    }

    private OperationResult<FanCurveProfile> Set83JJFanSpeedLevels(int[] levels)
    {
        if (!Win32BatteryInterop.IsAcOnlineQuick())
            return OperationResult<FanCurveProfile>.Fail("FAN_REQUIRES_AC", "Connect the AC adapter before changing fan levels.");
        using var other = GetSingleWmiInstance(LenovoWmiConstants.OtherMethodClass);
        if (Read83JJFeature(other, LenovoWmiConstants.CapFanFullSpeed) != 0 ||
            InvokeGameZoneGetter("GetSmartFanMode") != (uint)ThermalMode.Custom)
            return OperationResult<FanCurveProfile>.Fail("FAN_REQUIRES_CUSTOM", "Apply a Utility performance preset and stop full-speed cooling first.");

        var before = ReadFanCurveDirect();
        var baseline = before.LiveCapture?.Stages.Select(stage => stage.SpeedLevel).ToArray();
        if (baseline is null || !LenovoFanCapture.AreWritableLevels(baseline))
            return OperationResult<FanCurveProfile>.Fail("FAN_READ_FAILED", "The active fan curve could not be verified.");
        if (baseline.SequenceEqual(levels))
            return OperationResult<FanCurveProfile>.Ok(before);

        using var fan = GetSingleWmiInstance(LenovoWmiConstants.FanMethodClass);
        FanCurveProfile Write(int[] curve)
        {
            const string method = "Fan_Set_Table";
            var table = new byte[64];
            table[0] = 1;
            table[2] = (byte)curve.Length;
            for (var index = 0; index < curve.Length; index++)
                table[6 + 2 * index] = (byte)curve[index];
            using var input = fan.GetMethodParameters(method);
            input["FanTable"] = table;
            using var output = fan.InvokeMethod(method, input, null);
            for (var attempt = 0; attempt < 5; attempt++)
            {
                Thread.Sleep(100);
                var result = ReadFanCurveDirect();
                if (result.LiveCapture?.Stages.Select(stage => stage.SpeedLevel).SequenceEqual(curve) == true)
                    return result;
            }
            throw new ManagementException("Firmware fan levels did not match the written table.");
        }

        try
        {
            return OperationResult<FanCurveProfile>.Ok(Write(levels));
        }
        catch (Exception error)
        {
            try
            {
                Write(baseline);
                return OperationResult<FanCurveProfile>.Fail("ROLLED_BACK", error.Message);
            }
            catch (Exception restore)
            {
                return OperationResult<FanCurveProfile>.Fail("RESTORE_UNVERIFIED", $"{error.Message} {restore.Message}");
            }
        }
    }
}
