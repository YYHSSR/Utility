using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Utility.Core.Models;

namespace Utility.Platform.Native;

internal static class DeviceDriverInventory
{
    // Keys from devpkey.h. Property data is read from the active PnP device instance.
    private static readonly Guid Device = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    private static readonly Guid Instance = new("78c34fc8-104a-4aca-9ea4-524d52996e57");
    private static readonly Guid Driver = new("a8b865dd-2e3d-4094-ad97-e593a70c75d6");

    public static List<InstalledDriverEntry> Read(Func<string, string, string, DriverCategory> classify, CancellationToken ct)
    {
        const uint presentAllClasses = 0x02 | 0x04;
        using var devices = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, presentAllClasses);
        if (devices.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        using var enumerator = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum");
        var result = new List<InstalledDriverEntry>();
        var buffer = new byte[1024];
        var info = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
        for (uint index = 0; ; index++)
        {
            ct.ThrowIfCancellationRequested();
            if (!SetupDiEnumDeviceInfo(devices, index, ref info))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == 259) break; // ERROR_NO_MORE_ITEMS
                throw new Win32Exception(error);
            }

            // Keep the installed INF description (including vendor overrides) used by the WMI inventory.
            var instanceId = ReadText(Instance, 256);
            using var registration = instanceId.Length > 0 ? enumerator?.OpenSubKey(instanceId) : null;
            var name = registration?.GetValue("DeviceDesc") as string ?? ReadText(Device, 2);
            if (name.StartsWith('@'))
            {
                var separator = name.IndexOf(';');
                name = separator >= 0 ? name[(separator + 1)..] : ReadText(Device, 2);
            }
            name = name.Trim();
            if (name.Length == 0) continue;
            var provider = ReadText(Driver, 9);
            var version = ReadText(Driver, 3);
            var inf = ReadText(Driver, 5);
            var hardwareId = ReadText(Device, 3);
            var category = classify(name, provider, ReadText(Device, 9));
            var date = Read(Driver, 2, out var type) == 8 && type == 16
                ? DateTime.FromFileTimeUtc(BitConverter.ToInt64(buffer)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : "";
            result.Add(new(name, provider, version, date, inf, hardwareId, category));
        }
        return result;

        string ReadText(Guid set, uint id)
        {
            var size = Read(set, id, out var type);
            if (size < 2 || type is not (18 or 0x2012)) return "";
            var text = Encoding.Unicode.GetString(buffer, 0, size);
            var end = text.IndexOf('\0');
            return (end >= 0 ? text[..end] : text).Trim();
        }

        int Read(Guid set, uint id, out uint type)
        {
            var key = new PropertyKey(set, id);
            uint required;
            while (!SetupDiGetDevicePropertyW(devices, ref info, in key, out type,
                       buffer, (uint)buffer.Length, out required, 0))
            {
                var error = Marshal.GetLastWin32Error();
                if (error is 1168 or 13) return 0; // Optional property absent.
                if (error != 122 || required <= buffer.Length) throw new Win32Exception(error);
                buffer = new byte[required];
            }
            return checked((int)required);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfo { public uint Size; public Guid ClassGuid; public uint Instance; public UIntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey(Guid set, uint id)
    {
        public readonly Guid Set = set;
        public readonly uint Id = id;
    }

    private sealed class DeviceSet : SafeHandleZeroOrMinusOneIsInvalid
    {
        public DeviceSet() : base(true) { }
        protected override bool ReleaseHandle() => SetupDiDestroyDeviceInfoList(handle);
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern DeviceSet SetupDiGetClassDevsW(IntPtr classGuid, string? enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(DeviceSet devices, uint index, ref DeviceInfo info);

    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool SetupDiGetDevicePropertyW(DeviceSet devices, ref DeviceInfo info, in PropertyKey key,
        out uint type, [Out] byte[] buffer, uint size, out uint required, uint flags);

    [DllImport("setupapi.dll")]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devices);
}