using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using Microsoft.Win32;
using Utility.App.Localization;
using Utility.App.ViewModels;
using Utility.Core.Services;
using Utility.Platform.Providers;

namespace Utility.App;

public partial class App : Application
{
    private static readonly JsonSerializerOptions ExportJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private MainViewModel? _mainViewModel;
    private WindowsTelemetryProvider? _telemetryProvider;
    private UtilityTray? _tray;
    private Mutex? _instanceMutex;
    private EventWaitHandle? _showWindowEvent;
    private RegisteredWaitHandle? _showWindowRegistration;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var zhCulture = new CultureInfo("zh-CN");
        Loc.ActiveCulture = zhCulture;
        CultureInfo.DefaultThreadCurrentUICulture = zhCulture;
        Thread.CurrentThread.CurrentUICulture = zhCulture;

        var stateStore = new JsonStateStore();
        var validator = new ProfileValidationService();
        var battery = new WindowsBatteryProvider();
        _telemetryProvider = new WindowsTelemetryProvider();
        var software = new WindowsSoftwareProvider();
        var drivers = new WindowsDriverProvider();
        var hardware = new LenovoHardwareControlProvider(battery, _telemetryProvider);
        var coordinator = new HardwareWriteCoordinator(hardware, stateStore, validator);

        if (e.Args.Length == 2 && e.Args[0] is "--diagnostics-export" or "--read-fan-capture")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                var exitCode = 0;
                if (e.Args[0] == "--read-fan-capture")
                {
                    var result = await hardware.ReadFanCurveAsync(allowElevation: true).ConfigureAwait(false);
                    var capture = result.Success ? result.Value?.LiveCapture : null;
                    await File.WriteAllTextAsync(e.Args[1], JsonSerializer.Serialize(new
                    {
                        result.ErrorCode,
                        result.Message,
                        Capture = capture
                    }, ExportJsonOptions)).ConfigureAwait(false);
                    exitCode = capture is null ? 2 : 0;
                }
                else
                {
                    await ExportStandardUserDiagnosticsAsync(e.Args[1], stateStore, battery, _telemetryProvider, software, drivers, hardware)
                        .ConfigureAwait(false);
                }
                Dispatcher.Invoke(() => Shutdown(exitCode));
            }
            catch
            {
                Dispatcher.Invoke(() => Shutdown(1));
            }

            return;
        }

        var startInTray = e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase);
        if (!StartSingleInstance(showExisting: !startInTray))
        {
            Shutdown();
            return;
        }

        _mainViewModel = new MainViewModel(
            stateStore, validator, battery, _telemetryProvider, software, hardware, coordinator);

        // Optional: "--page <index>" opens a specific page (see MainViewModel.Nav* for indices).
        var pageArg = Array.IndexOf(e.Args, "--page");
        if (pageArg >= 0 && pageArg + 1 < e.Args.Length && int.TryParse(e.Args[pageArg + 1], out var page) && page is >= 0 and <= MainViewModel.NavLast)
            _mainViewModel.SelectedNavIndex = page;

        var window = new MainWindow(_mainViewModel);
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _tray = new UtilityTray(window, _mainViewModel, () => Shutdown());
        _showWindowRegistration = ThreadPool.RegisterWaitForSingleObject(_showWindowEvent!,
            (_, _) => Dispatcher.BeginInvoke(() => _tray?.ShowWindow()), null, Timeout.Infinite, executeOnlyOnce: false);
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SessionEnding += (_, _) => _mainViewModel.StopCooling();
        if (startInTray)
        {
            _mainViewModel.SetWindowVisibility(false);
            await _mainViewModel.RunSafelyAsync(_mainViewModel.InitializeAsync);
        }
        else
        {
            window.Show();
        }
    }

    private bool StartSingleInstance(bool showExisting)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var name = $@"Local\Utility.{identity.User?.Value}";
        _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, $"{name}.ShowWindow");
        var mutex = new Mutex(true, $"{name}.Instance", out var first);
        if (first)
        {
            _instanceMutex = mutex;
            return true;
        }

        mutex.Dispose();
        if (showExisting)
            _showWindowEvent.Set();
        return false;
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode is not (PowerModes.Resume or PowerModes.Suspend))
            return;

        Dispatcher.BeginInvoke(() =>
        {
            if (_mainViewModel is not { } vm)
                return;
            vm.StopCooling();
            if (e.Mode == PowerModes.Resume)
                _ = vm.RunSafelyAsync(vm.OnSystemResumeAsync);
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _showWindowRegistration?.Unregister(null);
        _showWindowEvent?.Dispose();
        _tray?.Dispose();
        _tray = null;
        _mainViewModel?.Dispose();
        _telemetryProvider?.Dispose();
        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static async Task ExportStandardUserDiagnosticsAsync(
        string outputPath,
        JsonStateStore stateStore,
        IBatteryProvider battery,
        ITelemetryProvider telemetry,
        ISoftwareInventoryProvider software,
        IDriverProvider drivers,
        ILenovoHardwareControlProvider hardware)
    {
        var settings = await stateStore.LoadSettingsAsync().ConfigureAwait(false);
        var capReport = await hardware.ProbeCapabilitiesAsync(allowElevation: false).ConfigureAwait(false);
        var thermal = await hardware.ReadLiveThermalSnapshotAsync(allowElevation: false).ConfigureAwait(false);
        var bat = await battery.ReadStatusAsync().ConfigureAwait(false);
        // Prime CPU & Disk delta counters briefly (read-only).
        _ = await telemetry.ReadSnapshotAsync(allowWakeDgpu: true).ConfigureAwait(false);
        await Task.Delay(350).ConfigureAwait(false);
        var tel = await telemetry.ReadSnapshotAsync(allowWakeDgpu: true).ConfigureAwait(false);
        var gfx = await hardware.ReadGraphicsStatusAsync(allowElevation: false).ConfigureAwait(false);
        var sw = await software.ScanAsync(includeLenovoLeftovers: true).ConfigureAwait(false);
        var drv = await drivers.ListDriversAsync().ConfigureAwait(false);

        var payload = new
        {
            ExportedAtUtc = DateTimeOffset.UtcNow,
            CapabilityReport = capReport,
            LiveThermal = thermal,
            Battery = bat,
            Telemetry = tel,
            Graphics = gfx,
            SoftwareCounts = new
            {
                InstalledApps = sw.InstalledApps.Count,
                StartupEntries = sw.StartupEntries.Count,
                LenovoLeftoverFindings = sw.LenovoFindings.Count
            },
            LenovoFindings = sw.LenovoFindings,
            DriverCount = drv.Count,
            SampleDrivers = drv.Take(20).ToList(),
            SeededProfilesCount = settings.Settings.Profiles.Count
        };

        var full = Path.GetFullPath(outputPath);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(full, JsonSerializer.Serialize(payload, ExportJsonOptions)).ConfigureAwait(false);
    }
}
