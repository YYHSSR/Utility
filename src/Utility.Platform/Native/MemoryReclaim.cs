using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Utility.Platform.Native;

public sealed record MemoryReclaimResult(int TrimmedProcesses, double? AvailableBeforeGiB, double? AvailableAfterGiB);

public static class MemoryReclaim
{
    private static readonly HashSet<string> ExcludedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Utility", "Utility.ElevatedWorker", "explorer", "sihost", "dwm", "ctfmon",
        "StartMenuExperienceHost", "ShellExperienceHost", "SearchHost", "TextInputHost"
    };

    public static async Task<MemoryReclaimResult> RunAsync()
    {
        using var self = Process.GetCurrentProcess();
        using var identity = WindowsIdentity.GetCurrent();
        var processes = Process.GetProcesses();
        try
        {
            var session = processes.Where(process => ReadProcess(() => process.SessionId) == self.SessionId).ToArray();
            var visibleApps = session.Where(HasWindow)
                .Select(process => ReadProcess(() => process.ProcessName))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var candidates = session.Select(process => (Process: process,
                    Name: ReadProcess(() => process.ProcessName),
                    Id: process.Id,
                    WorkingSet: ReadProcess(() => process.WorkingSet64),
                    CpuTime: ReadProcess(() => process.TotalProcessorTime)))
                .Where(item => item.Name is not null && item.CpuTime.HasValue &&
                    item.WorkingSet >= 64L * 1024 * 1024 && !visibleApps.Contains(item.Name) &&
                    !ExcludedProcesses.Contains(item.Name) && item.Id != self.Id)
                .OrderByDescending(item => item.WorkingSet)
                .Take(12)
                .ToArray();

            await Task.Delay(250).ConfigureAwait(false);
            var before = ReadAvailableMemory();
            var trimmed = 0;
            var windowsDirectory = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.Windows))
                + Path.DirectorySeparatorChar;
            foreach (var item in candidates)
            {
                item.Process.Refresh();
                var currentCpuTime = ReadProcess(() => item.Process.TotalProcessorTime);
                if (!currentCpuTime.HasValue || currentCpuTime - item.CpuTime > TimeSpan.FromMilliseconds(15) ||
                    HasWindow(item.Process))
                    continue;

                var image = ReadProcess(() => item.Process.MainModule?.FileName);
                if (image is null || image.StartsWith(windowsDirectory, StringComparison.OrdinalIgnoreCase))
                    continue;

                using var handle = OpenProcess(0x1100, false, item.Id); // QUERY_LIMITED_INFORMATION | SET_QUOTA
                if (handle.IsInvalid || !OpenProcessToken(handle, 0x0008, out var token))
                    continue;

                using (token)
                using (var owner = new WindowsIdentity(token.DangerousGetHandle()))
                {
                    if (owner.User == identity.User && GetExitCodeProcess(handle, out var exitCode) && exitCode == 259 &&
                        EmptyWorkingSet(handle))
                        trimmed++;
                }
            }

            return new MemoryReclaimResult(trimmed, before, ReadAvailableMemory());
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    private static double? ReadAvailableMemory() =>
        SystemMetricsInterop.ReadMemoryUsage() is { } memory ? memory.TotalGb - memory.UsedGb : null;

    private static bool HasWindow(Process process) => ReadProcess(() => process.MainWindowHandle) is null or not 0;

    private static T? ReadProcess<T>(Func<T> read) where T : struct
    {
        try { return read(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

    private static string? ReadProcess(Func<string?> read)
    {
        try { return read(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

    [DllImport("kernel32.dll", EntryPoint = "K32EmptyWorkingSet", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(SafeProcessHandle process);
}
