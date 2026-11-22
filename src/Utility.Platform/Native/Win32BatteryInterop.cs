using System.Runtime.InteropServices;
using Utility.Core.Models;

namespace Utility.Platform.Native;

/// <summary>Reads Windows AC/DC status, battery capacities, health, and cycle count via native Win32 APIs.</summary>
public static class Win32BatteryInterop
{
    private static readonly Guid GuidDevClassBattery = new(0x72631e54, 0x78a4, 0x11d0, 0xbc, 0xf7, 0x00, 0xaa, 0x00, 0xb7, 0xb3, 0x2a);
    private const uint DigcfPresent = 0x02;
    private const uint DigcfDeviceInterface = 0x10;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareReadWrite = 0x03;
    private const uint OpenExisting = 0x03;
    private const uint FileAttributeNormal = 0x80;
    private const uint IoctlBatteryQueryTag = 0x294040;
    private const uint IoctlBatteryQueryInformation = 0x294044;
    private const uint IoctlBatteryQueryStatus = 0x29404C;

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BatteryQueryInformation
    {
        public uint BatteryTag;
        public uint InformationLevel;
        public int AtRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BatteryInformation
    {
        public uint Capabilities;
        public byte Technology;
        public byte Reserved0;
        public byte Reserved1;
        public byte Reserved2;
        public uint Chemistry;
        public uint DesignedCapacity;
        public uint FullChargedCapacity;
        public uint DefaultAlert1;
        public uint DefaultAlert2;
        public uint CriticalBias;
        public uint CycleCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BatteryWaitStatus
    {
        public uint BatteryTag;
        public uint Timeout;
        public uint PowerState;
        public uint LowCapacity;
        public uint HighCapacity;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BatteryStatus
    {
        public uint PowerState;
        public uint Capacity;
        public uint Voltage;
        public int Rate;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus lpSystemPowerStatus);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr devInfo, IntPtr devData, ref Guid interfaceClassGuid, uint memberIndex, ref SpDeviceInterfaceData devInterfaceData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr devInfo, ref SpDeviceInterfaceData devInterfaceData, IntPtr detailData, uint detailDataSize, out uint requiredSize, IntPtr devInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devInfo);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode, IntPtr lpInBuffer, uint nInBufferSize, IntPtr lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    public sealed record NativeBatteryDetails(
        bool IsAcOnline,
        int? ChargePercent,
        BatteryChargeActivity Activity,
        int? DesignCapacityMwh,
        int? FullChargeCapacityMwh,
        int? RemainingCapacityMwh,
        int? CycleCount,
        int? VoltageMv,
        int? ChargeRateMw,
        string Note);

    public static bool IsAcOnlineQuick()
    {
        return GetSystemPowerStatus(out var sps) && sps.ACLineStatus == 1;
    }

    public static NativeBatteryDetails ReadNativeBatteryDetails()
    {
        bool acOnline = false;
        int? chargePct = null;
        var activity = BatteryChargeActivity.Unknown;

        if (GetSystemPowerStatus(out var sps))
        {
            acOnline = sps.ACLineStatus == 1;
            if (sps.BatteryLifePercent <= 100)
            {
                chargePct = sps.BatteryLifePercent;
            }

            if ((sps.BatteryFlag & 8) != 0)
            {
                activity = BatteryChargeActivity.Charging;
            }
            else if ((sps.BatteryFlag & 4) != 0)
            {
                activity = BatteryChargeActivity.Critical;
            }
            else if (!acOnline)
            {
                activity = BatteryChargeActivity.Discharging;
            }
            else
            {
                activity = BatteryChargeActivity.IdleOnAc;
            }
        }

        var devicePath = TryFindFirstBatteryDevicePath();
        if (devicePath is null)
        {
            return new NativeBatteryDetails(acOnline, chargePct, activity, null, null, null, null, null, null, "Win32 GetSystemPowerStatus only (no battery device interface found).");
        }

        var handle = CreateFile(devicePath, GenericRead | GenericWrite, FileShareReadWrite, IntPtr.Zero, OpenExisting, FileAttributeNormal, IntPtr.Zero);
        if (handle == new IntPtr(-1))
        {
            handle = CreateFile(devicePath, GenericRead, FileShareReadWrite, IntPtr.Zero, OpenExisting, FileAttributeNormal, IntPtr.Zero);
        }

        if (handle == new IntPtr(-1))
        {
            return new NativeBatteryDetails(acOnline, chargePct, activity, null, null, null, null, null, null, $"Battery handle open failed (Win32={Marshal.GetLastWin32Error()}).");
        }

        try
        {
            var tag = QueryBatteryTag(handle);
            if (tag == 0)
            {
                return new NativeBatteryDetails(acOnline, chargePct, activity, null, null, null, null, null, null, "Battery tag query returned 0.");
            }

            var info = QueryBatteryInformation(handle, tag);
            var status = QueryBatteryStatus(handle, tag);

            int? design = info is { DesignedCapacity: > 0 and not 0xFFFFFFFF } ? (int)info.Value.DesignedCapacity : null;
            int? full = info is { FullChargedCapacity: > 0 and not 0xFFFFFFFF } ? (int)info.Value.FullChargedCapacity : null;
            int? cycles = info is { CycleCount: not 0xFFFFFFFF } ? (int)info.Value.CycleCount : null;
            int? remaining = status is { Capacity: not 0xFFFFFFFF } ? (int)status.Value.Capacity : null;
            int? voltage = status is { Voltage: > 0 and not 0xFFFFFFFF } ? (int)status.Value.Voltage : null;
            int? rate = status is not null && status.Value.Rate != unchecked((int)0x80000000) ? status.Value.Rate : null;

            if (status is not null)
            {
                if ((status.Value.PowerState & 0x04) != 0)
                {
                    activity = BatteryChargeActivity.Charging;
                }
                else if ((status.Value.PowerState & 0x02) != 0)
                {
                    activity = BatteryChargeActivity.Discharging;
                }
                else if ((status.Value.PowerState & 0x01) != 0)
                {
                    activity = BatteryChargeActivity.IdleOnAc;
                }
            }

            return new NativeBatteryDetails(acOnline, chargePct, activity, design, full, remaining, cycles, voltage, rate, "Win32 SetupAPI + IOCTL_BATTERY_QUERY_INFORMATION");
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static string? TryFindFirstBatteryDevicePath()
    {
        var guid = GuidDevClassBattery;
        var hDevInfo = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (hDevInfo == IntPtr.Zero || hDevInfo == new IntPtr(-1))
        {
            return null;
        }

        try
        {
            var data = new SpDeviceInterfaceData { cbSize = (uint)Marshal.SizeOf<SpDeviceInterfaceData>() };
            if (!SetupDiEnumDeviceInterfaces(hDevInfo, IntPtr.Zero, ref guid, 0, ref data))
            {
                return null;
            }

            SetupDiGetDeviceInterfaceDetail(hDevInfo, ref data, IntPtr.Zero, 0, out var required, IntPtr.Zero);
            if (required < 8)
            {
                return null;
            }

            var buffer = Marshal.AllocHGlobal((int)required);
            try
            {
                Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                if (!SetupDiGetDeviceInterfaceDetail(hDevInfo, ref data, buffer, required, out _, IntPtr.Zero))
                {
                    return null;
                }

                return Marshal.PtrToStringUni(IntPtr.Add(buffer, 4));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(hDevInfo);
        }
    }

    private static uint QueryBatteryTag(IntPtr handle)
    {
        var pWait = Marshal.AllocHGlobal(4);
        var pTag = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(pWait, 0);
            if (!DeviceIoControl(handle, IoctlBatteryQueryTag, pWait, 4, pTag, 4, out _, IntPtr.Zero))
            {
                return 0;
            }

            return (uint)Marshal.ReadInt32(pTag);
        }
        finally
        {
            Marshal.FreeHGlobal(pWait);
            Marshal.FreeHGlobal(pTag);
        }
    }

    private static BatteryInformation? QueryBatteryInformation(IntPtr handle, uint tag)
    {
        var query = new BatteryQueryInformation { BatteryTag = tag, InformationLevel = 0, AtRate = 0 };
        var inSize = Marshal.SizeOf<BatteryQueryInformation>();
        var outSize = Marshal.SizeOf<BatteryInformation>();
        var pIn = Marshal.AllocHGlobal(inSize);
        var pOut = Marshal.AllocHGlobal(outSize);
        try
        {
            Marshal.StructureToPtr(query, pIn, false);
            if (!DeviceIoControl(handle, IoctlBatteryQueryInformation, pIn, (uint)inSize, pOut, (uint)outSize, out _, IntPtr.Zero))
            {
                return null;
            }

            return Marshal.PtrToStructure<BatteryInformation>(pOut);
        }
        finally
        {
            Marshal.FreeHGlobal(pIn);
            Marshal.FreeHGlobal(pOut);
        }
    }

    private static BatteryStatus? QueryBatteryStatus(IntPtr handle, uint tag)
    {
        var wait = new BatteryWaitStatus { BatteryTag = tag };
        var inSize = Marshal.SizeOf<BatteryWaitStatus>();
        var outSize = Marshal.SizeOf<BatteryStatus>();
        var pIn = Marshal.AllocHGlobal(inSize);
        var pOut = Marshal.AllocHGlobal(outSize);
        try
        {
            Marshal.StructureToPtr(wait, pIn, false);
            if (!DeviceIoControl(handle, IoctlBatteryQueryStatus, pIn, (uint)inSize, pOut, (uint)outSize, out _, IntPtr.Zero))
            {
                return null;
            }

            return Marshal.PtrToStructure<BatteryStatus>(pOut);
        }
        finally
        {
            Marshal.FreeHGlobal(pIn);
            Marshal.FreeHGlobal(pOut);
        }
    }
}
