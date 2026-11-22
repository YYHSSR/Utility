using System.Runtime.InteropServices;
using Utility.Core.Models;

namespace Utility.Platform.Native;

/// <summary>
/// Interfaces with Lenovo ACPI-Compliant Virtual Power Controller (\\.\EnergyDrv)
/// using IOCTL 0x831020F8 to read and toggle battery conservation mode.
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

    public static uint[] BuildTransitionCommands(BatteryChargeMode current, BatteryChargeMode target) =>
        (current, target) switch
        {
            (BatteryChargeMode.RapidCharge, BatteryChargeMode.Conservation) => [0x08u, 0x03u],
            (_, BatteryChargeMode.Conservation) => [0x03u],
            (BatteryChargeMode.Conservation, BatteryChargeMode.Normal) => [0x05u],
            (BatteryChargeMode.RapidCharge, BatteryChargeMode.Normal) => [0x08u],
            (_, BatteryChargeMode.Normal) => [0x05u],
            (BatteryChargeMode.Conservation, BatteryChargeMode.RapidCharge) => [0x05u, 0x07u],
            (_, BatteryChargeMode.RapidCharge) => [0x07u],
            _ => []
        };

    public static EnergyDrvReadResult ReadChargeMode()
    {
        var handle = OpenDriver(requireWrite: false, out var openErr);
        if (handle == new IntPtr(-1))
        {
            return new EnergyDrvReadResult(false, BatteryChargeMode.Unknown, 0, openErr, $"Failed to open {DevicePath} (Win32={openErr}).");
        }

        try
        {
            var inBuf = QuerySelector;
            if (!DeviceIoControl(handle, IoctlEnergyBatteryChargeMode, ref inBuf, 4, out var outBuf, 4, out _, IntPtr.Zero))
            {
                var err = Marshal.GetLastWin32Error();
                return new EnergyDrvReadResult(false, BatteryChargeMode.Unknown, 0, err, $"EnergyDrv query IOCTL failed (Win32={err}).");
            }

            var mode = DecodeBatteryChargeMode(outBuf);
            return new EnergyDrvReadResult(true, mode, outBuf, 0, $"EnergyDrv IOCTL 0x{IoctlEnergyBatteryChargeMode:X8} -> 0x{outBuf:X8} ({mode}).");
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public static EnergyDrvReadResult SetConservationMode(bool enabled)
    {
        var current = ReadChargeMode();
        if (!current.Success)
        {
            return current;
        }

        var target = enabled ? BatteryChargeMode.Conservation : BatteryChargeMode.Normal;
        if (current.Mode == target)
        {
            return current with { Note = $"Conservation state already matches target ({target})." };
        }

        var commands = BuildTransitionCommands(current.Mode, target);
        var handle = OpenDriver(requireWrite: true, out var openErr);
        if (handle == new IntPtr(-1))
        {
            return new EnergyDrvReadResult(false, current.Mode, current.RawValue, openErr, $"Failed to open {DevicePath} RW (Win32={openErr}).");
        }

        try
        {
            foreach (var cmd in commands)
            {
                var inBuf = cmd;
                if (!DeviceIoControl(handle, IoctlEnergyBatteryChargeMode, ref inBuf, 4, out _, 4, out _, IntPtr.Zero))
                {
                    var err = Marshal.GetLastWin32Error();
                    return new EnergyDrvReadResult(false, BatteryChargeMode.Unknown, 0, err, $"EnergyDrv write command 0x{cmd:X2} failed (Win32={err}).");
                }
            }
        }
        finally
        {
            CloseHandle(handle);
        }

        return ReadChargeMode();
    }

    private static IntPtr OpenDriver(bool requireWrite, out int lastError)
    {
        var access = requireWrite ? (GenericRead | GenericWrite) : (GenericRead | GenericWrite);
        var h = CreateFile(DevicePath, access, FileShareReadWrite, IntPtr.Zero, OpenExisting, FileAttributeNormal, IntPtr.Zero);
        if (h == new IntPtr(-1) && !requireWrite)
        {
            h = CreateFile(DevicePath, GenericRead, FileShareReadWrite, IntPtr.Zero, OpenExisting, FileAttributeNormal, IntPtr.Zero);
        }

        lastError = h == new IntPtr(-1) ? Marshal.GetLastWin32Error() : 0;
        return h;
    }
}
