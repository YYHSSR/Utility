using System.Globalization;
using System.IO;
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
        var hardware = new LenovoHardwareControlProvider(battery, _telemetryProvider, stateStore);
        var coordinator = new HardwareWriteCoordinator(hardware, stateStore, validator);

        if (e.Args.Length == 2 && e.Args[0] == "--diagnostics-export")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                await ExportStandardUserDiagnosticsAsync(e.Args[1], stateStore, battery, _telemetryProvider, software, drivers, hardware)
                    .ConfigureAwait(false);
                Dispatcher.Invoke(() => Shutdown(0));
            }
            catch
            {
                Dispatcher.Invoke(() => Shutdown(1));
            }

            return;
        }

        _mainViewModel = new MainViewModel(
            stateStore, validator, battery, _telemetryProvider, software, drivers, hardware, coordinator);

        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        var window = new MainWindow(_mainViewModel);
        MainWindow = window;
        window.Show();
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume && _mainViewModel is not null)
        {
            _ = _mainViewModel.OnSystemResumeAsync();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _mainViewModel?.Dispose();
        _telemetryProvider?.Dispose();
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
