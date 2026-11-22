using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Utility.Platform.Native;

/// <summary>Collects all Windows counters in one PDH query, independent of the Windows display language.</summary>
internal sealed class PerformanceQuery : IDisposable
{
    private sealed class QueryHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public QueryHandle() : base(true) { }
        protected override bool ReleaseHandle() => PdhCloseQuery(handle) == 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterValue { public uint Status; public double Value; }
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? source, nuint data, out QueryHandle query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(QueryHandle query, string path, nuint data, out IntPtr counter);
    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(QueryHandle query);
    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr type, out CounterValue result);

    private QueryHandle? _query;
    private IntPtr _diskTime, _diskBytes, _packagePower;
    private bool _primed;
    private long _retryAfter;

    public (double? DiskTime, double? DiskBytes, double? PackageMilliwatts) Read()
    {
        if (_query is null)
        {
            if (Environment.TickCount64 < _retryAfter)
                return default;
            if (PdhOpenQueryW(null, 0, out var handle) != 0)
            {
                handle?.Dispose();
                _retryAfter = Environment.TickCount64 + 30000;
                return default;
            }
            _query = handle;
            IntPtr Add(string path) => PdhAddEnglishCounterW(handle, path, 0, out var counter) == 0 ? counter : IntPtr.Zero;
            _diskTime = Add(@"\PhysicalDisk(_Total)\% Disk Time");
            _diskBytes = Add(@"\PhysicalDisk(_Total)\Disk Bytes/sec");
            _packagePower = Add(@"\Energy Meter(RAPL_Package0_PKG)\Power");
        }
        if (PdhCollectQueryData(_query) != 0)
        {
            Dispose();
            _retryAfter = Environment.TickCount64 + 5000;
            return default;
        }
        if (!_primed)
        {
            _primed = true;
            return default;
        }
        return (Value(_diskTime), Value(_diskBytes), Value(_packagePower));
    }

    private static double? Value(IntPtr counter) =>
        counter != IntPtr.Zero && PdhGetFormattedCounterValue(counter, 0x200 | 0x8000, IntPtr.Zero, out var result) == 0 &&
        result.Status <= 1 && double.IsFinite(result.Value) ? result.Value : null;

    public void Dispose()
    {
        _query?.Dispose();
        _query = null;
        _primed = false;
    }
}
