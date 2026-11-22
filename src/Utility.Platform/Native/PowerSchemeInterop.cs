using System.Runtime.InteropServices;
using Utility.Core.Models;

namespace Utility.Platform.Native;

/// <summary>Reads and updates Windows active power scheme and power overlay mode via powrprof.dll.</summary>
public static class PowerSchemeInterop
{
    private static readonly Guid BestPowerEfficiencyOverlay = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    private static readonly Guid BalancedOverlay = Guid.Empty;
    private static readonly Guid BestPerformanceOverlay = new("ded574b5-45a0-4f42-8737-46345c09c238");

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadFriendlyName(
        IntPtr rootPowerKey,
        ref Guid schemeGuid,
        IntPtr subGroupOfPowerSettingsGuid,
        IntPtr powerSettingGuid,
        IntPtr buffer,
        ref uint bufferSize);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActualOverlayScheme(out Guid actualOverlayGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveOverlayScheme(Guid overlaySchemeGuid);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    public static (WindowsPowerMode Mode, string SchemeName) ReadWindowsPowerState()
    {
        var schemeName = ReadActiveSchemeName();
        try
        {
            var status = PowerGetActualOverlayScheme(out var overlay);
            if (status == 0)
            {
                return (FromOverlayGuid(overlay), schemeName);
            }
        }
        catch (EntryPointNotFoundException)
        {
            // Older or customized power configurations may not expose the overlay export.
        }
        catch
        {
            // Ignore and return unknown overlay mode.
        }

        return (WindowsPowerMode.Unknown, schemeName);
    }

    public static bool TrySetWindowsPowerMode(WindowsPowerMode mode, out string detail)
    {
        if (mode == WindowsPowerMode.Unknown)
        {
            detail = "Cannot apply Unknown Windows power mode.";
            return false;
        }

        var guid = ToOverlayGuid(mode);
        try
        {
            var hr = PowerSetActiveOverlayScheme(guid);
            if (hr == 0)
            {
                detail = $"Applied Windows power mode overlay {mode}.";
                return true;
            }

            detail = $"PowerSetActiveOverlayScheme returned 0x{hr:X8} (active power plan may not support Windows overlay slider).";
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            detail = "PowerSetActiveOverlayScheme export unavailable on this Windows configuration.";
            return false;
        }
        catch (Exception ex)
        {
            detail = $"Failed to set Windows power mode: {ex.Message}";
            return false;
        }
    }

    private static string ReadActiveSchemeName()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var ptr) != 0 || ptr == IntPtr.Zero)
        {
            return "Unknown";
        }

        try
        {
            var guid = Marshal.PtrToStructure<Guid>(ptr);
            uint size = 0;
            PowerReadFriendlyName(IntPtr.Zero, ref guid, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size);
            if (size == 0)
            {
                return guid.ToString();
            }

            var buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PowerReadFriendlyName(IntPtr.Zero, ref guid, IntPtr.Zero, IntPtr.Zero, buf, ref size) == 0)
                {
                    return Marshal.PtrToStringUni(buf) ?? guid.ToString();
                }

                return guid.ToString();
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        finally
        {
            LocalFree(ptr);
        }
    }

    private static WindowsPowerMode FromOverlayGuid(Guid guid)
    {
        if (guid == BestPowerEfficiencyOverlay) return WindowsPowerMode.BestPowerEfficiency;
        if (guid == BalancedOverlay) return WindowsPowerMode.Balanced;
        if (guid == BestPerformanceOverlay) return WindowsPowerMode.BestPerformance;
        return WindowsPowerMode.Unknown;
    }

    private static Guid ToOverlayGuid(WindowsPowerMode mode) => mode switch
    {
        WindowsPowerMode.BestPowerEfficiency => BestPowerEfficiencyOverlay,
        WindowsPowerMode.Balanced => BalancedOverlay,
        WindowsPowerMode.BestPerformance => BestPerformanceOverlay,
        _ => BalancedOverlay
    };
}
