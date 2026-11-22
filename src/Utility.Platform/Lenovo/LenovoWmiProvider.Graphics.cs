using System.Management;
using Utility.Core.Models;
using Utility.Core.Services;

namespace Utility.Platform.Lenovo;

public sealed partial class LenovoWmiProvider
{
    public GraphicsStatusSnapshot ReadGraphicsStatusDirect()
    {
        var now = DateTimeOffset.UtcNow;
        try
        {
            if (!IsKnown83JJFirmware())
                return UnknownGraphics(now, "GRAPHICS_UNSUPPORTED");

            using var instance = GetSingleWmiInstance(LenovoWmiConstants.GameZoneDataClass);
            if (!Has83JJGraphicsControls(instance))
                return UnknownGraphics(now, "GRAPHICS_UNSUPPORTED");

            using var other = GetSingleWmiInstance(LenovoWmiConstants.OtherMethodClass);
            var state = Read83JJGraphicsSettings(instance, other);
            return Describe83JJGraphics(state, now);
        }
        catch (Exception ex) when (!IsProcessElevated() &&
            ex is UnauthorizedAccessException or ManagementException or System.Runtime.InteropServices.COMException)
        {
            return UnknownGraphics(now, "GRAPHICS_REQUIRES_ADMIN");
        }
        catch (Exception ex)
        {
            return UnknownGraphics(now, ex.Message);
        }
    }

    public OperationResult<GraphicsStatusSnapshot> SetGraphicsModeDirect(GraphicsRoutingMode? mode)
    {
        if (!IsKnown83JJFirmware() || mode is not
            (GraphicsRoutingMode.Hybrid or GraphicsRoutingMode.DgpuOnly or GraphicsRoutingMode.HybridIgpuOnly))
            return OperationResult<GraphicsStatusSnapshot>.Fail("GRAPHICS_UNSUPPORTED", "Unsupported graphics mode or firmware.");

        return Run83JJWrite(() => Set83JJGraphicsMode(mode.Value));
    }

    private static OperationResult<GraphicsStatusSnapshot> Set83JJGraphicsMode(GraphicsRoutingMode mode)
    {
        using var instance = GetSingleWmiInstance(LenovoWmiConstants.GameZoneDataClass);
        if (!Has83JJGraphicsControls(instance))
            return OperationResult<GraphicsStatusSnapshot>.Fail("GRAPHICS_UNSUPPORTED", "Firmware graphics controls are unavailable.");

        using var other = GetSingleWmiInstance(LenovoWmiConstants.OtherMethodClass);
        var baseline = Read83JJGraphicsSettings(instance, other);
        var before = Describe83JJGraphics(baseline, DateTimeOffset.UtcNow);
        if (!before.IsVerifiedLiveRead || before.BootSession is null)
            return OperationResult<GraphicsStatusSnapshot>.Fail("GRAPHICS_READ_FAILED", "Windows display adapters could not be verified.");

        var target = (Dgpu: mode == GraphicsRoutingMode.DgpuOnly ? 1u : 0u,
            Igpu: mode == GraphicsRoutingMode.HybridIgpuOnly ? 1u : 0u);
        try
        {
            Write83JJGraphicsSettings(instance, other, target);
            var current = Describe83JJGraphics(Read83JJGraphicsSettings(instance, other), DateTimeOffset.UtcNow);
            // During adapter re-enumeration Windows can briefly report neither GPU. Keep that result pending.
            return OperationResult<GraphicsStatusSnapshot>.Ok(current with
            {
                PendingMode = current.EffectiveMode == mode ? null : mode,
                RebootRequired = current.EffectiveMode != mode &&
                    (target.Dgpu != baseline.Dgpu || mode == GraphicsRoutingMode.HybridIgpuOnly),
                BootSession = before.BootSession
            });
        }
        catch (Exception applyError)
        {
            try
            {
                Write83JJGraphicsSettings(instance, other, baseline);
                return OperationResult<GraphicsStatusSnapshot>.Fail("ROLLED_BACK", applyError.Message);
            }
            catch (Exception recoveryError)
            {
                return OperationResult<GraphicsStatusSnapshot>.Fail("RESTORE_UNVERIFIED",
                    $"{applyError.Message} Firmware graphics restore failed: {recoveryError.Message}");
            }
        }
    }

    private static bool Has83JJGraphicsControls(ManagementObject instance) =>
        ReadGraphicsValue(instance, "IsSupportGSync") == 2 && ReadGraphicsValue(instance, "IsSupportIGPUMode") == 3;

    // On this BIOS, GameZone hides the configured integrated-GPU submode while the MUX
    // is discrete. The Other Method feature reports the stored submode in either MUX state.
    private const uint IgpuModeFeature = 0x00010000;

    private static (uint Dgpu, uint Igpu) Read83JJGraphicsSettings(ManagementObject instance, ManagementObject other)
    {
        var dgpu = ReadGraphicsValue(instance, "GetGSyncStatus");
        var igpu = Read83JJFeature(other, IgpuModeFeature) ?? uint.MaxValue;
        if (dgpu > 1 || igpu > 2)
            throw new ManagementException($"Unexpected graphics values: {dgpu}, {igpu}.");

        return (dgpu, igpu);
    }

    private static uint ReadGraphicsValue(ManagementObject instance, string method)
    {
        using var output = instance.InvokeMethod(method, null, null);
        return output?["Data"] is uint value ? value : throw new ManagementException($"{method} returned no graphics value.");
    }

    private static void Write83JJGraphicsSettings(ManagementObject instance, ManagementObject other, (uint Dgpu, uint Igpu) target)
    {
        if (Read83JJFeature(other, IgpuModeFeature) != target.Igpu)
        {
            const string method = "SetFeatureValue";
            using var input = other.GetMethodParameters(method);
            input["IDs"] = IgpuModeFeature;
            input["value"] = target.Igpu;
            using var output = other.InvokeMethod(method, input, null);
            VerifyFirmwareValue(() => Read83JJFeature(other, IgpuModeFeature) ?? uint.MaxValue, target.Igpu, "iGPU preference");
        }

        // The MUX getter reads the booted EC state. The setter queues the next boot's
        // routing via firmware and has no readable pre-reboot result.
        const string muxSetter = "SetGSyncStatus";
        using var muxInput = instance.GetMethodParameters(muxSetter);
        muxInput["Data"] = target.Dgpu;
        using var muxOutput = instance.InvokeMethod(muxSetter, muxInput, null);
    }

    private static GraphicsStatusSnapshot Describe83JJGraphics((uint Dgpu, uint Igpu) state, DateTimeOffset now)
    {
        var configured = state switch
        {
            (1, _) => GraphicsRoutingMode.DgpuOnly,
            (0, 1) => GraphicsRoutingMode.HybridIgpuOnly,
            (0, 2) => GraphicsRoutingMode.HybridAuto,
            _ => GraphicsRoutingMode.Hybrid
        };
        var intel = false;
        var nvidia = false;
        try
        {
            using var query = new ManagementObjectSearcher(LenovoWmiConstants.CimNamespace,
                "SELECT PNPDeviceID, ConfigManagerErrorCode FROM Win32_VideoController");
            using var adapters = query.Get();
            foreach (ManagementObject adapter in adapters)
            {
                using (adapter)
                {
                    if (adapter["ConfigManagerErrorCode"] is not uint error || error != 0 ||
                        adapter["PNPDeviceID"] is not string id)
                        continue;
                    intel |= id.StartsWith(@"PCI\VEN_8086", StringComparison.OrdinalIgnoreCase);
                    nvidia |= id.StartsWith(@"PCI\VEN_10DE", StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return UnknownGraphics(now, ex.Message) with { PendingMode = configured };
        }

        var effective = (intel, nvidia) switch
        {
            (true, true) => configured == GraphicsRoutingMode.HybridAuto ? configured : GraphicsRoutingMode.Hybrid,
            (true, false) => configured == GraphicsRoutingMode.HybridAuto ? configured : GraphicsRoutingMode.HybridIgpuOnly,
            (false, true) => GraphicsRoutingMode.DgpuOnly,
            _ => GraphicsRoutingMode.Unknown
        };
        var pending = effective == configured ? (GraphicsRoutingMode?)null : configured;
        var reboot = pending.HasValue && effective != GraphicsRoutingMode.Unknown &&
                     (configured == GraphicsRoutingMode.DgpuOnly || effective == GraphicsRoutingMode.DgpuOnly ||
                      configured == GraphicsRoutingMode.HybridIgpuOnly);
        return new GraphicsStatusSnapshot(effective, pending, reboot,
            [
                new(GraphicsRoutingMode.Hybrid, effective == GraphicsRoutingMode.DgpuOnly, true, true, "Hybrid"),
                new(GraphicsRoutingMode.HybridIgpuOnly, effective != GraphicsRoutingMode.HybridIgpuOnly, true, true, "Integrated GPU preference"),
                new(GraphicsRoutingMode.DgpuOnly, effective != GraphicsRoutingMode.DgpuOnly, true, true, "Discrete GPU only")
            ],
            effective != GraphicsRoutingMode.Unknown, effective == GraphicsRoutingMode.Unknown ? "GRAPHICS_UNAVAILABLE" : string.Empty, now)
        {
            BootSession = ReadBootSession()
        };
    }

    // A running process cannot survive a reboot. Retry failed reads, cache successful ones.
    private static string? _bootSession;

    private static string? ReadBootSession()
    {
        try
        {
            if (Volatile.Read(ref _bootSession) is { } known) return known;
            using var query = new ManagementObjectSearcher(LenovoWmiConstants.CimNamespace,
                "SELECT LastBootUpTime FROM Win32_OperatingSystem");
            using var items = query.Get();
            foreach (ManagementObject os in items)
            {
                using (os)
                {
                    var value = os["LastBootUpTime"] as string;
                    if (!string.IsNullOrEmpty(value))
                        Volatile.Write(ref _bootSession, value);
                    return value;
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
        }
        return null;
    }

    private static GraphicsStatusSnapshot UnknownGraphics(DateTimeOffset now, string reason) =>
        new(GraphicsRoutingMode.Unknown, null, false, [], false, reason, now);
}
