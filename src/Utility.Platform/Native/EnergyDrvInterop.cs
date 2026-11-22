using System.Runtime.InteropServices;
using Utility.Core.Models;
using Utility.Core.Services;

namespace Utility.Platform.Native;

/// <summary>
/// Interfaces with Lenovo ACPI-Compliant Virtual Power Controller (\\.\EnergyDrv)
/// using IOCTL 0x831020F8 for battery charge policy.
/// </summary>
public static class EnergyDrvInterop
{
    private const string DevicePath = @"\\.\EnergyDrv";
    private const uint IoctlEnergyBatteryChargeMode = 0x831020F8;
    private const uint QuerySelector = 0xFF;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareReadWrite = 0x03;
    private const uint OpenExisting = 0x03;
    private const uint FileAttributeNormal = 0x80;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        IntPtr hDevice,
        uint dwIoControlCode,
        ref uint lpInBuffer,
        uint nInBufferSize,
        out uint lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    public sealed record EnergyDrvReadResult(
        bool Success,
        BatteryChargeMode Mode,
        uint RawValue,
        int Win32Error,
        string Note);

    public static BatteryChargeMode DecodeBatteryChargeMode(uint rawState)
    {
        // The 83JJ reports the persistent charge policy in the low byte even while plugged in.
        if ((rawState & 0x20) != 0)
            return BatteryChargeMode.Conservation;
        if ((rawState & 0x04) != 0)
            return BatteryChargeMode.RapidCharge;

        var isCharging = (rawState & (1u << 17)) != 0;
        var isRapidCharge = (rawState & (1u << 26)) != 0;
        var isConservation = (rawState & (1u << 29)) != 0;

        if (isCharging)
        {
            return isRapidCharge ? BatteryChargeMode.RapidCharge : BatteryChargeMode.Normal;
        }

        if (isConservation)
        {
            return BatteryChargeMode.Conservation;
        }

        return BatteryChargeMode.Normal;
    }

    public static EnergyDrvReadResult ReadChargeMode()
    {
        var handle = OpenDriver(out var openErr);
        if (handle == new IntPtr(-1))
        {
            return new EnergyDrvReadResult(false, BatteryChargeMode.Unknown, 0, openErr, $"Failed to open {DevicePath} (Win32={openErr}).");
        }

        try
        {
            return ReadChargeMode(handle);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static EnergyDrvReadResult ReadChargeMode(IntPtr handle)
    {
        var input = QuerySelector;
        if (!DeviceIoControl(handle, IoctlEnergyBatteryChargeMode, ref input, 4, out var output, 4, out var bytes, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            return new(false, BatteryChargeMode.Unknown, 0, error, $"EnergyDrv query failed (Win32={error}).");
        }

        if (bytes != sizeof(uint))
            return new(false, BatteryChargeMode.Unknown, 0, 0, "EnergyDrv returned an incomplete state value.");

        var mode = DecodeBatteryChargeMode(output);
        return new(true, mode, output, 0, $"EnergyDrv IOCTL 0x{IoctlEnergyBatteryChargeMode:X8} -> 0x{output:X8} ({mode}).");
    }

    internal static OperationResult<BatteryChargeMode> SetConservationMode83JJ(bool enabled)
    {
        var handle = CreateFile(DevicePath, GenericRead | GenericWrite, FileShareReadWrite, IntPtr.Zero,
            OpenExisting, FileAttributeNormal, IntPtr.Zero);
        if (handle == new IntPtr(-1))
            return OperationResult<BatteryChargeMode>.Fail("BATTERY_WRITE_UNAVAILABLE",
                $"Could not open EnergyDrv for writing (Win32={Marshal.GetLastWin32Error()}).");
        try
        {
            var baseline = ReadChargeMode(handle);
            if (!baseline.Success)
                return OperationResult<BatteryChargeMode>.Fail("BATTERY_READ_FAILED", baseline.Note);
            var target = enabled ? BatteryChargeMode.Conservation : BatteryChargeMode.Normal;
            if (baseline.Mode == target || (!enabled && baseline.Mode == BatteryChargeMode.RapidCharge))
                return OperationResult<BatteryChargeMode>.Ok(baseline.Mode);

            void WriteMode(BatteryChargeMode mode)
            {
                uint[] selectors = mode switch
                {
                    BatteryChargeMode.Conservation => [8, 3],
                    BatteryChargeMode.Normal => [5, 8],
                    BatteryChargeMode.RapidCharge => [5, 7],
                    _ => throw new InvalidOperationException("Unknown battery charge policy.")
                };
                foreach (var selector in selectors)
                {
                    var input = selector;
                    if (!DeviceIoControl(handle, IoctlEnergyBatteryChargeMode, ref input, 4, out _, 4, out _, IntPtr.Zero))
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                }

                for (var attempt = 0; attempt < 6; attempt++)
                {
                    var read = ReadChargeMode(handle);
                    if (read.Success && read.Mode == mode)
                        return;
                    Thread.Sleep(100);
                }
                throw new InvalidOperationException($"Could not verify the {mode} charge policy.");
            }

            try
            {
                WriteMode(target);
                return OperationResult<BatteryChargeMode>.Ok(target);
            }
            catch (Exception error)
            {
                try
                {
                    WriteMode(baseline.Mode);
                    return OperationResult<BatteryChargeMode>.Fail("ROLLED_BACK", error.Message);
                }
                catch (Exception restore)
                {
                    return OperationResult<BatteryChargeMode>.Fail("RESTORE_UNVERIFIED", $"{error.Message} {restore.Message}");
                }
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static IntPtr OpenDriver(out int lastError)
    {
        var h = CreateFile(DevicePath, GenericRead | GenericWrite, FileShareReadWrite, IntPtr.Zero, OpenExisting, FileAttributeNormal, IntPtr.Zero);
        if (h == new IntPtr(-1))
        {
            h = CreateFile(DevicePath, GenericRead, FileShareReadWrite, IntPtr.Zero, OpenExisting, FileAttributeNormal, IntPtr.Zero);
        }

        lastError = h == new IntPtr(-1) ? Marshal.GetLastWin32Error() : 0;
        return h;
    }
}
