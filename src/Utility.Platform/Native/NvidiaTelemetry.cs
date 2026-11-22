using System.Runtime.InteropServices;
using System.Text;
using Utility.Core.Models;

namespace Utility.Platform.Native;

internal sealed record NvidiaSample(string? Name, SensorReading<double> Utilization, SensorReading<double> Temperature,
    SensorReading<double> PowerWatts, SensorReading<double> MemoryUsedMb, SensorReading<double> MemoryTotalMb, string? PowerState);

internal sealed class NvidiaTelemetry : IDisposable
{
    // Resolve only the installed driver in System32, including when running elevated.
    private const string Library = "nvml.dll";
    [StructLayout(LayoutKind.Sequential)]
    private struct Utilization { public uint Gpu, Memory; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Memory { public ulong Total, Free, Used; }
    [DllImport(Library, ExactSpelling = true, CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int nvmlInit_v2();
    [DllImport(Library, ExactSpelling = true, CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int nvmlShutdown();
    [DllImport(Library, ExactSpelling = true, CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [DllImport(Library, ExactSpelling = true, CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int nvmlDeviceGetName(IntPtr device, [Out] StringBuilder name, uint size);
    [DllImport(Library, ExactSpelling = true, CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out Utilization utilization);
    [DllImport(Library, ExactSpelling = true, CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int nvmlDeviceGetTemperature(IntPtr device, uint sensor, out uint value);
    [DllImport(Library, ExactSpelling = true, CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
    [DllImport(Library, ExactSpelling = true, CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out Memory memory);
    [DllImport(Library, ExactSpelling = true, CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int nvmlDeviceGetPerformanceState(IntPtr device, out uint state);

    private bool _initialized;
    private IntPtr _device;
    private string? _name;

    public NvidiaSample? TryRead(DateTimeOffset now)
    {
        try
        {
            if (!_initialized)
            {
                if (nvmlInit_v2() != 0)
                    return null;
                _initialized = true;
                if (nvmlDeviceGetHandleByIndex_v2(0, out _device) != 0)
                {
                    Dispose();
                    return null;
                }
                var name = new StringBuilder(96);
                _name = nvmlDeviceGetName(_device, name, (uint)name.Capacity) == 0 ? name.ToString() : null;
            }
            var device = _device;
            SensorReading<double> Metric(bool available, double value, string unit) =>
                new(available ? value : null, unit, available ? SensorStatus.Valid : SensorStatus.Unavailable, "NVIDIA NVML", now);
            var hasUtil = nvmlDeviceGetUtilizationRates(device, out var utilization) == 0;
            var hasTemp = nvmlDeviceGetTemperature(device, 0, out var temperature) == 0;
            var hasPower = nvmlDeviceGetPowerUsage(device, out var power) == 0;
            var hasMemory = nvmlDeviceGetMemoryInfo(device, out var memory) == 0;
            var hasState = nvmlDeviceGetPerformanceState(device, out var state) == 0;
            if (!hasUtil && !hasTemp && !hasMemory && !hasPower)
            {
                Dispose();
                return null;
            }
            return new NvidiaSample(_name, Metric(hasUtil, utilization.Gpu, "%"),
                Metric(hasTemp, temperature, "C"), Metric(hasPower, power / 1000d, "W"),
                Metric(hasMemory, memory.Used / 1048576d, "MB"), Metric(hasMemory, memory.Total / 1048576d, "MB"),
                hasState && state <= 15 ? $"P{state}" : null);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            Dispose();
            return null;
        }
    }

    public void Dispose()
    {
        if (_initialized)
        {
            nvmlShutdown();
            _initialized = false;
            _device = IntPtr.Zero;
            _name = null;
        }
    }
}
