using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Utility.Platform.Providers;

/// <summary>
/// Raises a (debounced) callback whenever a Run key or a StartupApproved flag changes, so a startup entry the
/// user turned off can be turned off again the moment its app re-enables itself. Read-only: it only watches.
/// </summary>
internal sealed class StartupRegistryWatcher : IDisposable
{
    private const uint NotifyName = 0x1, NotifyLastSet = 0x4, NotifyThreadAgnostic = 0x10000000;
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);

    private readonly Action _changed;
    private readonly List<(RegistryKey Key, AutoResetEvent Signal, bool Subtree)> _watches = [];
    private readonly List<(RegistryKey Key, AutoResetEvent Signal, bool Subtree)> _armed;
    private readonly ManualResetEvent _stop = new(false);
    private readonly Thread _thread;

    public StartupRegistryWatcher(Action changed)
    {
        _changed = changed;
        const string run = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";
        Add(RegistryHive.CurrentUser, RegistryView.Registry64, run, false);
        Add(RegistryHive.CurrentUser, RegistryView.Registry64, approved, true);
        Add(RegistryHive.LocalMachine, RegistryView.Registry64, run, false);
        Add(RegistryHive.LocalMachine, RegistryView.Registry32, run, false);
        Add(RegistryHive.LocalMachine, RegistryView.Registry64, approved, true);
        // Arm before returning (thread-agnostic notifications), so no change made right after construction is missed.
        _armed = _watches.Where(Arm).ToList();

        _thread = new Thread(Loop) { IsBackground = true, Name = "Utility startup watcher" };
        _thread.Start();
    }

    private void Add(RegistryHive hive, RegistryView view, string path, bool subtree)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            // HKCU\…\StartupApproved may not exist yet on a fresh profile; create it so it can be watched.
            var key = hive == RegistryHive.CurrentUser ? baseKey.CreateSubKey(path, writable: false) : baseKey.OpenSubKey(path);
            if (key is not null)
                _watches.Add((key, new AutoResetEvent(false), subtree));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // Not watchable for this user; the periodic sweep still covers it.
        }
    }

    private bool Arm((RegistryKey Key, AutoResetEvent Signal, bool Subtree) watch) =>
        RegNotifyChangeKeyValue(watch.Key.Handle, watch.Subtree, NotifyName | NotifyLastSet | NotifyThreadAgnostic,
            watch.Signal.SafeWaitHandle, true) == 0;

    private void Loop()
    {
        var armed = _armed;
        if (armed.Count == 0)
            return;
        var handles = armed.Select(w => (WaitHandle)w.Signal).Prepend(_stop).ToArray();
        while (true)
        {
            var hit = WaitHandle.WaitAny(handles);
            if (hit == 0)
                return;
            // Re-arm first so a change made while the callback runs is not missed; then let a burst settle.
            Arm(armed[hit - 1]);
            if (_stop.WaitOne(Debounce))
                return;
            for (var i = 0; i < armed.Count; i++)
                if (armed[i].Signal.WaitOne(0))
                    Arm(armed[i]);
            try
            {
                _changed();
            }
            catch
            {
                // The callback must never take the watcher thread down.
            }
        }
    }

    public void Dispose()
    {
        _stop.Set();
        _thread.Join(TimeSpan.FromSeconds(2));
        foreach (var (key, signal, _) in _watches)
        {
            key.Dispose();
            signal.Dispose();
        }
        _stop.Dispose();
    }

    [DllImport("advapi32.dll")]
    private static extern int RegNotifyChangeKeyValue(SafeRegistryHandle hKey, bool bWatchSubtree, uint dwNotifyFilter,
        SafeWaitHandle hEvent, bool fAsynchronous);
}
