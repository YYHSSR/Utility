using System.Runtime.InteropServices;

namespace Utility.Platform.Native;

/// <summary>Native Windows CPU time and physical memory telemetry helpers.</summary>
public static class SystemMetricsInterop
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
        public ulong ToUInt64() => ((ulong)HighDateTime << 32) | LowDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    public sealed record CpuTimeSample(ulong Idle, ulong Kernel, ulong User);

    public static CpuTimeSample? SampleCpuTimes()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return null;
        }

        return new CpuTimeSample(idle.ToUInt64(), kernel.ToUInt64(), user.ToUInt64());
    }

    public static double? ComputeCpuUtilization(CpuTimeSample previous, CpuTimeSample current)
    {
        var idleDelta = current.Idle - previous.Idle;
        var kernelDelta = current.Kernel - previous.Kernel;
        var userDelta = current.User - previous.User;
        var total = kernelDelta + userDelta;
        if (total == 0)
        {
            return null;
        }

        var busy = total > idleDelta ? total - idleDelta : 0;
        return Math.Clamp(busy * 100.0 / total, 0.0, 100.0);
    }

    public static (double UsedGb, double TotalGb, double UsedPercent)? ReadMemoryUsage()
    {
        var ms = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref ms) || ms.ullTotalPhys == 0)
        {
            return null;
        }

        const double bytesPerGb = 1024.0 * 1024.0 * 1024.0;
        var totalGb = ms.ullTotalPhys / bytesPerGb;
        var usedBytes = ms.ullTotalPhys - ms.ullAvailPhys;
        var usedGb = usedBytes / bytesPerGb;
        var pct = usedBytes * 100.0 / ms.ullTotalPhys;
        return (usedGb, totalGb, pct);
    }
}
