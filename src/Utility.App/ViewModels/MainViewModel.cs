using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using Utility.App.Localization;
using Utility.Core.Models;
using Utility.Core.Services;
using Utility.Platform.Lenovo;
using Utility.Platform.Native;

namespace Utility.App.ViewModels;

/// <summary>Main coordinator ViewModel backing Utility's single-window navigation and 7 features.</summary>
public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private readonly JsonStateStore _stateStore;
    private readonly ProfileValidationService _validator;
    private readonly IBatteryProvider _battery;
    private readonly ITelemetryProvider _telemetry;
    private readonly ISoftwareInventoryProvider _software;
    private readonly ILenovoHardwareControlProvider _hardware;
    private readonly HardwareWriteCoordinator _writeCoordinator;
    private readonly System.Windows.Threading.DispatcherTimer _telemetryTimer;
    private readonly SemaphoreSlim _settingsSaveLock = new(1, 1);
    private readonly SemaphoreSlim _telemetryGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;
    private bool _initialUiReady, _isTargetLegion;
    private readonly bool _isElevated = LenovoWmiProvider.IsProcessElevated();
    private readonly SemaphoreSlim _nextSensorSample = new(0, 1);
    private bool _monitorAttempted, _monitorRunning, _monitorHasSample;
    private bool _batteryRefreshPending;
    private long _nextBatteryTick;
    private long _lastDetailTick, _lastSensorTick;
    private SystemMetricsInterop.CpuTimeSample? _previousCpu = SystemMetricsInterop.SampleCpuTimes();
    private readonly HashSet<int> _requestedInventoryPages = [];

    private AppSettingsState _settings = new();
    private LiveThermalSnapshot? _latestThermalSnapshot;
    // Background thermal reads that started before this moment may show the pre-switch profile,
    // so they are ignored to keep the dashboard and the tray on the state the last write verified.
    private DateTimeOffset _lastHardwareWriteUtc = DateTimeOffset.MinValue;

    private string? _interruptedOpId;
    private bool _isWindowVisible = true;
    private bool _isHardwareBusy;
    private CancellationTokenSource? _coolingStop;

    private int _selectedNavIndex;
    private string _machineBannerText = "LENOVO 83JJ";
    private string _statusMessage = string.Empty;
    private bool _hasInterruptedOperation;
    private string _interruptedOperationSummary = string.Empty;

    // F1 Profiles
    private string _selectedProfileId = ProfileSeedFactory.DailyId;
    private string _liveThermalModeText = "--";
    private string _liveThermalNoteText = string.Empty;
    private string _liveWindowsPowerModeText = "--";
    private string _liveAcStateText = "--";

    // F2 Battery
    private string _batteryChargeText = "--";
    private string _batteryActivityText = "--";
    private string _batteryHealthText = "--";
    private string _batteryDesignCapText = "--";
    private string _batteryFullCapText = "--";
    private string _batteryCycleText = "--";
    private string _batteryConservationText = "--";
    private string _batteryRawDetailText = string.Empty;
    private BatteryStatusSnapshot? _batterySnapshot;

    // F3 Telemetry
    private bool _pauseGpuOnBattery = true;
    private string _cpuUsageText = "--";
    private string _cpuTempText = "--";
    private string _memoryUsageText = "--";
    private string _diskActivityText = "--";
    private string _gpuNameText = "NVIDIA GeForce RTX 4060 Laptop GPU";
    private string _gpuUtilText = "--";
    private string _gpuTempText = "--";
    private string _gpuPowerText = "--";
    private string _cpuPowerText = "--";
    private string _gpuVramText = "--";
    private string _gpuPstateText = "--";
    private string _cpuFanRpmText = "--";
    private string _gpuFanRpmText = "--";
    private string _telemetryTimestampText = "--";
    private double _cpuUsagePercent = double.NaN;
    private double _memoryUsedPercent = double.NaN;
    private double _gpuUtilPercent = double.NaN;
    private double _diskActivePercent = double.NaN;
    private string _memoryShortText = "--";
    private const int HistoryCapacity = 120;
    private readonly Queue<double> _cpuHistoryBuffer = new();
    private readonly Queue<double> _gpuHistoryBuffer = new();
    private IReadOnlyList<double> _cpuHistory = [];
    private IReadOnlyList<double> _gpuHistory = [];
    private bool _isOnAcPower;
    private double _batteryChargePercent = double.NaN;

    // F4 Graphics
    private string _effectiveGraphicsText = "--";
    private string _pendingGraphicsText = "--";
    private bool _hasPendingGraphicsReboot;
    private string _graphicsNoteText = string.Empty;
    private GraphicsStatusSnapshot? _graphicsSnapshot;
    private GraphicsModeOption? _selectedGraphicsMode;

    // F6 Fan curves
    private string _fanStatusBannerText = Loc.Get("MsgPendingFanCapture");
    private string _fanValidationText = string.Empty;
    private PointCollection _fanPreviewPoints = [];
    private FanChannelDraft? _selectedFanChannel;
    private string _firmwareFanTableText = string.Empty;
    private LenovoFanCapture? _liveFanCapture;
    private IReadOnlyList<LenovoFanStage> _fanStages = LenovoFanCapture.OwnerReference.Stages;
    private string _fanCaptureStatus = Loc.Get("FanCaptureReference");
    private string _fanLevelText = string.Empty;

    public MainViewModel(
        JsonStateStore stateStore,
        ProfileValidationService validator,
        IBatteryProvider battery,
        ITelemetryProvider telemetry,
        ISoftwareInventoryProvider software,
        ILenovoHardwareControlProvider hardware,
        HardwareWriteCoordinator writeCoordinator)
    {
        _stateStore = stateStore;
        _validator = validator;
        _battery = battery;
        _telemetry = telemetry;
        _software = software;
        _hardware = hardware;
        _writeCoordinator = writeCoordinator;

        AsyncRelayCommand Command(Func<Task> action) => new(() => RunSafelyAsync(action));

        NavigateCommand = new RelayCommand(p => { if (p is string s && int.TryParse(s, out var idx)) SelectedNavIndex = idx; });
        DismissInterruptedOperationCommand = Command(DismissInterruptedAsync);

        SelectProfileCommand = new RelayCommand(p => { if (p is string id) SelectProfile(id); });
        SaveProfileDraftCommand = Command(SaveProfileDraftAsync);
        ResetProfileToReferenceCommand = Command(ResetProfileToReferenceAsync);
        RefreshLiveThermalCommand = Command(() => RefreshThermalAsync(allowElevation: true));
        ApplyProfilePowerCommand = new(
            parameter => RunSafelyAsync(() => ApplyProfilePowerAsync(parameter as string ?? SelectedProfileId)),
            _ => !IsHardwareBusy && !HasInterruptedOperation);
        // One-click cleanup runs on the 垃圾清理 page; its summary goes to the toast / tray balloon.
        QuickCleanupCommand = new AsyncRelayCommand(() =>
        {
            SelectedNavIndex = NavCleanup;
            return RunSafelyAsync(Cleanup.RunAsync);
        }, () => !Cleanup.IsCleaning && !Cleanup.IsDuplicateBusy);
        Cleanup.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(QuickCleanupViewModel.IsCleaning) or nameof(QuickCleanupViewModel.IsDuplicateBusy))
                QuickCleanupCommand.RaiseCanExecuteChanged();
        };
        // The 垃圾清理 page shows its own results; toast only when that page is not open
        // (the tray balloon still covers a hidden window).
        Cleanup.Completed += summary =>
        {
            if (SelectedNavIndex != NavCleanup)
                StatusMessage = summary;
            QuickActionCompleted?.Invoke(summary);
        };
        // Uninstall results surface in the auto-hiding toast.
        _uninstall = new Lazy<CleanupViewModel>(() =>
        {
            var model = new CleanupViewModel();
            model.StatusReported += status => StatusMessage = status;
            return model;
        });
        StartCoolingCommand = new(() => RunSafelyAsync(StartCoolingAsync), () => !IsHardwareBusy && !HasInterruptedOperation);
        StopCoolingCommand = new(StopCooling, () => _coolingStop is { IsCancellationRequested: false });
        RefreshBatteryCommand = Command(RefreshBatteryAsync);
        SetConservationCommand = new(parameter => RunSafelyAsync(() => SetConservationAsync(parameter is "on")),
            parameter => !IsHardwareBusy && _batterySnapshot?.IsConservationWriteSupported == true &&
                         (parameter is "on" ? BatteryIsConservationOff : !BatteryIsConservationOff));
        RefreshAllCommand = Command(RefreshAllAsync);
        EnableFirmwareSensorsCommand = new(() => RunSafelyAsync(RunSensorMonitorAsync), () => !_isElevated && !_monitorRunning);

        ManualRefreshTelemetryCommand = Command(() => RefreshTelemetryAsync(allowWakeDgpu: true));
        RefreshGraphicsCommand = Command(() => RefreshGraphicsAsync(allowElevation: true));
        ApplyGraphicsModeCommand = new(() => RunSafelyAsync(ApplyGraphicsModeAsync),
            () => !IsHardwareBusy && !HasInterruptedOperation && SelectedGraphicsMode is { } selection &&
                  (_graphicsSnapshot?.IsVerifiedLiveRead != true || selection.Mode != CurrentGraphicsMode));
        ApplyFanLevelsCommand = new(parameter => RunSafelyAsync(() => ApplyFanLevelsAsync(parameter is "restore")),
            _ => !IsHardwareBusy && !HasInterruptedOperation && _liveFanCapture is not null);
        PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(IsHardwareBusy) or nameof(HasInterruptedOperation))
            {
                ApplyProfilePowerCommand.RaiseCanExecuteChanged();
                StartCoolingCommand.RaiseCanExecuteChanged();
                ApplyGraphicsModeCommand.RaiseCanExecuteChanged();
                SetConservationCommand.RaiseCanExecuteChanged();
                ApplyFanLevelsCommand.RaiseCanExecuteChanged();
            }
        };

        OpenWindowsStartupCommand = new RelayCommand(() => _software.OpenWindowsStartupApps());

        AddFanPointCommand = new RelayCommand(AddFanPoint);
        ValidateFanPreviewCommand = new RelayCommand(UpdateFanCurvePreview);
        ReadFanTableCommand = Command(ReadFanFromFirmwareAsync);
        AddFanChannelCommand = new RelayCommand(AddFanChannel);
        SaveFanDraftCommand = Command(SaveFanDraftAsync);
        SaveFanCaptureCommand = new AsyncRelayCommand(
            () => RunSafelyAsync(SaveFanCaptureAsync), () => _liveFanCapture is not null);

        _telemetryTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _telemetryTimer.Tick += (_, _) => SampleVisiblePage();
    }

    public ObservableCollection<ProfileFieldRow> ProfileFields { get; } = [];
    public ObservableCollection<DiskCapacityInfo> DiskCapacities { get; } = [];
    private IReadOnlyList<StartupEntryRow> _allStartupEntries = [];
    private bool _showScheduledTasks;

    /// <summary>Rows shown on the startup tab: apps that start at sign-in, plus logon tasks when asked for.</summary>
    public IReadOnlyList<StartupEntryRow> StartupEntries { get; private set; } = [];

    /// <summary>Also list scheduled tasks that run at sign-in/boot (mostly Windows' own maintenance tasks).</summary>
    public bool ShowScheduledTasks
    {
        get => _showScheduledTasks;
        set
        {
            if (SetField(ref _showScheduledTasks, value))
                FilterStartupEntries();
        }
    }

    /// <summary>"另有 N 个计划任务" hint next to the toggle (empty when shown or none).</summary>
    public string ScheduledTaskSummary { get; private set; } = string.Empty;

    private void FilterStartupEntries()
    {
        StartupEntries = _allStartupEntries
            .Where(row => ShowScheduledTasks || row.Source != StartupEntrySource.ScheduledTask)
            // Enabled first, then by name: what actually starts with Windows is on top.
            .OrderByDescending(row => row.IsEnabled).ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var hidden = _allStartupEntries.Count - StartupEntries.Count;
        ScheduledTaskSummary = hidden > 0 ? Loc.Format("StartupTasksHidden", hidden) : string.Empty;
        RaisePropertyChanged(nameof(StartupEntries));
        RaisePropertyChanged(nameof(ScheduledTaskSummary));
    }
    public ObservableCollection<FanChannelDraft> FanChannels { get; } = [];
    public IReadOnlyList<string> FanSpeedUnits { get; } = ["RPM", "%", "Level"];
    public LenovoDriverUpdatesViewModel DriverUpdates { get; } = new();

    // Page indices, in sidebar order. Keep in sync with the TabItems in MainWindow.xaml.
    public const int NavDashboard = 0;
    public const int NavProfiles = 1;
    public const int NavFanCurves = 2;
    public const int NavBattery = 3;
    // Merged into the battery page; kept so existing page arguments (e.g. --page 4) still resolve.
    public const int NavGraphics = 4;
    public const int NavCleanup = 5;
    public const int NavSoftware = 6;
    public const int NavDrivers = 7;
    public const int NavLast = NavDrivers;

    public RelayCommand NavigateCommand { get; }
    public AsyncRelayCommand RefreshAllCommand { get; }
    public AsyncRelayCommand EnableFirmwareSensorsCommand { get; }
    public string FirmwareMonitorText => Loc.Get(_monitorHasSample || _isElevated ? "MsgSensorsEnabled" : "BtnEnableFirmwareSensors");
    public AsyncRelayCommand DismissInterruptedOperationCommand { get; }
    public RelayCommand SelectProfileCommand { get; }
    public AsyncRelayCommand SaveProfileDraftCommand { get; }
    public AsyncRelayCommand ResetProfileToReferenceCommand { get; }
    public AsyncRelayCommand RefreshLiveThermalCommand { get; }
    public AsyncRelayCommand ApplyProfilePowerCommand { get; }
    /// <summary>垃圾清理 page (one-click cleanup, C drive incl. restore points, duplicates, large files).</summary>
    internal QuickCleanupViewModel Cleanup { get; } = new();

    /// <summary>Separate instance for the uninstall tab on the software page (independent of the cleanup page).</summary>
    private readonly Lazy<CleanupViewModel> _uninstall;
    internal CleanupViewModel Uninstall => _uninstall.Value;

    /// <summary>DataContext for the uninstall tab (CleanupViewModel is internal, so it is exposed as object).</summary>
    public object UninstallContext => Uninstall;
    public AsyncRelayCommand QuickCleanupCommand { get; }
    public AsyncRelayCommand StartCoolingCommand { get; }
    public RelayCommand StopCoolingCommand { get; }
    public bool IsHardwareBusy { get => _isHardwareBusy; private set => SetField(ref _isHardwareBusy, value); }
    public string? ActiveProfileId => _latestThermalSnapshot?.MatchedProfileId;
    public event Action<string>? QuickActionCompleted;
    public AsyncRelayCommand RefreshBatteryCommand { get; }
    public AsyncRelayCommand SetConservationCommand { get; }
    public AsyncRelayCommand ApplyGraphicsModeCommand { get; }
    public AsyncRelayCommand ApplyFanLevelsCommand { get; }
    public AsyncRelayCommand ManualRefreshTelemetryCommand { get; }
    public AsyncRelayCommand RefreshGraphicsCommand { get; }
    public RelayCommand OpenWindowsStartupCommand { get; }
    public RelayCommand AddFanPointCommand { get; }
    public RelayCommand ValidateFanPreviewCommand { get; }
    public AsyncRelayCommand ReadFanTableCommand { get; }
    public RelayCommand AddFanChannelCommand { get; }
    public AsyncRelayCommand SaveFanDraftCommand { get; }
    public AsyncRelayCommand SaveFanCaptureCommand { get; }

    public int SelectedNavIndex
    {
        get => _selectedNavIndex;
        set
        {
            // The graphics mode card now lives on the battery page.
            if (value == NavGraphics)
                value = NavBattery;
            if (value is < 0 or > NavLast || !SetField(ref _selectedNavIndex, value) || !_initialUiReady)
                return;
            UpdateSampling();
            _ = RunSafelyAsync(() => LoadSelectedInventoryAsync(value));
        }
    }

    public string MachineBannerText { get => _machineBannerText; set => SetField(ref _machineBannerText, value); }
    /// <summary>Last action feedback. Re-raised on every set (even with the same text) so the toast re-appears.</summary>
    public string StatusMessage
    {
        get => _statusMessage;
        set
        {
            _statusMessage = value ?? string.Empty;
            RaisePropertyChanged();
        }
    }
    public bool HasInterruptedOperation { get => _hasInterruptedOperation; set => SetField(ref _hasInterruptedOperation, value); }
    public string InterruptedOperationSummary { get => _interruptedOperationSummary; set => SetField(ref _interruptedOperationSummary, value); }

    public string SelectedProfileId
    {
        get => _selectedProfileId;
        set
        {
            if (SetField(ref _selectedProfileId, value))
                RaisePropertyChanged(nameof(SelectedProfileDisplayName));
        }
    }

    public string SelectedProfileDisplayName => SelectedProfileId == ProfileSeedFactory.GamingId
        ? Loc.Get("ProfileGamingName")
        : Loc.Get("ProfileDailyName");

    public string AppVersionText { get; } =
        "v" + (typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

    public bool IsOnAcPower { get => _isOnAcPower; set => SetField(ref _isOnAcPower, value); }
    public double BatteryChargePercent { get => _batteryChargePercent; set => SetField(ref _batteryChargePercent, value); }
    public double CpuUsagePercent { get => _cpuUsagePercent; set => SetField(ref _cpuUsagePercent, value); }
    public double MemoryUsedPercent { get => _memoryUsedPercent; set => SetField(ref _memoryUsedPercent, value); }
    public double GpuUtilPercent { get => _gpuUtilPercent; set => SetField(ref _gpuUtilPercent, value); }
    public double DiskActivePercent { get => _diskActivePercent; set => SetField(ref _diskActivePercent, value); }
    public string MemoryShortText { get => _memoryShortText; set => SetField(ref _memoryShortText, value); }
    public IReadOnlyList<double> CpuHistory { get => _cpuHistory; private set => SetField(ref _cpuHistory, value); }
    public IReadOnlyList<double> GpuHistory { get => _gpuHistory; private set => SetField(ref _gpuHistory, value); }
    public string LiveThermalModeText { get => _liveThermalModeText; set => SetField(ref _liveThermalModeText, value); }
    public string LiveThermalNoteText { get => _liveThermalNoteText; set => SetField(ref _liveThermalNoteText, value); }
    public string LiveWindowsPowerModeText { get => _liveWindowsPowerModeText; set => SetField(ref _liveWindowsPowerModeText, value); }
    public string LiveAcStateText { get => _liveAcStateText; set => SetField(ref _liveAcStateText, value); }
    public string BatteryChargeText { get => _batteryChargeText; set => SetField(ref _batteryChargeText, value); }
    public string BatteryActivityText { get => _batteryActivityText; set => SetField(ref _batteryActivityText, value); }
    public string BatteryHealthText { get => _batteryHealthText; set => SetField(ref _batteryHealthText, value); }
    public string BatteryDesignCapText { get => _batteryDesignCapText; set => SetField(ref _batteryDesignCapText, value); }
    public string BatteryFullCapText { get => _batteryFullCapText; set => SetField(ref _batteryFullCapText, value); }
    public string BatteryCycleText { get => _batteryCycleText; set => SetField(ref _batteryCycleText, value); }
    public string BatteryConservationText { get => _batteryConservationText; set => SetField(ref _batteryConservationText, value); }
    public string BatteryRawDetailText { get => _batteryRawDetailText; set => SetField(ref _batteryRawDetailText, value); }

    public bool PauseGpuOnBattery
    {
        get => _pauseGpuOnBattery;
        set
        {
            if (SetField(ref _pauseGpuOnBattery, value))
            {
                _ = RunSafelyAsync(() => UpdateSettingsAsync(settings => settings with { PauseGpuPollingOnBattery = value }));
            }
        }
    }

    public string CpuUsageText { get => _cpuUsageText; set => SetField(ref _cpuUsageText, value); }
    public string CpuTempText { get => _cpuTempText; set => SetField(ref _cpuTempText, value); }
    public string MemoryUsageText { get => _memoryUsageText; set => SetField(ref _memoryUsageText, value); }
    public string DiskActivityText { get => _diskActivityText; set => SetField(ref _diskActivityText, value); }
    public string GpuNameText { get => _gpuNameText; set => SetField(ref _gpuNameText, value); }
    public string GpuUtilText { get => _gpuUtilText; set => SetField(ref _gpuUtilText, value); }
    public string GpuTempText { get => _gpuTempText; set => SetField(ref _gpuTempText, value); }
    public string GpuPowerText { get => _gpuPowerText; set => SetField(ref _gpuPowerText, value); }
    public string CpuPowerText { get => _cpuPowerText; set => SetField(ref _cpuPowerText, value); }
    public string GpuVramText { get => _gpuVramText; set => SetField(ref _gpuVramText, value); }
    public string GpuPstateText { get => _gpuPstateText; set => SetField(ref _gpuPstateText, value); }
    public string CpuFanRpmText { get => _cpuFanRpmText; set => SetField(ref _cpuFanRpmText, value); }
    public string GpuFanRpmText { get => _gpuFanRpmText; set => SetField(ref _gpuFanRpmText, value); }
    public string TelemetryTimestampText { get => _telemetryTimestampText; set => SetField(ref _telemetryTimestampText, value); }

    public string EffectiveGraphicsText { get => _effectiveGraphicsText; set => SetField(ref _effectiveGraphicsText, value); }
    public string PendingGraphicsText { get => _pendingGraphicsText; set => SetField(ref _pendingGraphicsText, value); }
    public bool HasPendingGraphicsReboot { get => _hasPendingGraphicsReboot; set => SetField(ref _hasPendingGraphicsReboot, value); }
    public string GraphicsNoteText { get => _graphicsNoteText; set => SetField(ref _graphicsNoteText, value); }

    public FanChannelDraft? SelectedFanChannel
    {
        get => _selectedFanChannel;
        set
        {
            if (SetField(ref _selectedFanChannel, value))
                UpdateFanCurvePreview();
        }
    }

    public string FirmwareFanTableText { get => _firmwareFanTableText; set => SetField(ref _firmwareFanTableText, value); }
    public IReadOnlyList<LenovoFanStage> FanStages { get => _fanStages; private set => SetField(ref _fanStages, value); }
    public string FanCaptureStatus { get => _fanCaptureStatus; private set => SetField(ref _fanCaptureStatus, value); }
    public string FanLevelText { get => _fanLevelText; set => SetField(ref _fanLevelText, value); }

    public string FanStatusBannerText { get => _fanStatusBannerText; set => SetField(ref _fanStatusBannerText, value); }
    public string FanValidationText { get => _fanValidationText; set => SetField(ref _fanValidationText, value); }
    public PointCollection FanPreviewPoints { get => _fanPreviewPoints; set => SetField(ref _fanPreviewPoints, value); }

    private Task? _initialization;
    public Task InitializeAsync() => _initialization ??= InitializeCoreAsync();

    private async Task InitializeCoreAsync()
    {
        if (SelectedNavIndex == NavDashboard && _isWindowVisible)
            ReadQuickMetrics();
        var loaded = await Task.Run(() => _stateStore.LoadSettingsAsync(_lifetime.Token)).ConfigureAwait(false);
        _settings = loaded.Settings;

        var interrupted = await _writeCoordinator.GetInterruptedOperationAsync().ConfigureAwait(false);
        var identity = await _hardware.GetMachineIdentityAsync().ConfigureAwait(false);

        await RunOnUiAsync(() =>
        {
            _isTargetLegion = identity.IsTargetLegion83JJ;
            // Sidebar device card shows the Windows computer name (e.g. LAPTOP-XXXX).
            MachineBannerText = Environment.MachineName;
            _pauseGpuOnBattery = _settings.PauseGpuPollingOnBattery;

            RaisePropertyChanged(nameof(PauseGpuOnBattery));

            if (loaded.RecoveredFromLastGood)
            {
                StatusMessage = Loc.Get("MsgRecoveredLastGood");
            }

            if (interrupted is not null)
            {
                _interruptedOpId = interrupted.OperationId;
                HasInterruptedOperation = true;
                InterruptedOperationSummary = $"{interrupted.OperationType} ({interrupted.TargetDescription}) @ {interrupted.StartedUtc.LocalDateTime:g}";
            }

            SelectProfile(_settings.ActiveSelectedProfileId);
            if (_disposed)
                return;
            _initialUiReady = true;
            UpdateSampling();
            _ = RunSafelyAsync(() => RefreshThermalAsync(allowElevation: false));
            _ = RunSafelyAsync(() => LoadSelectedInventoryAsync(SelectedNavIndex));
        });
    }

    private async Task LoadSelectedInventoryAsync(int index)
    {
        Func<Task>? load = index switch
        {
            NavBattery => () => RefreshGraphicsAsync(allowElevation: false),
            NavSoftware => async () =>
            {
                await RunOnUiAsync(() => Uninstall.RefreshAppsCommand.Execute(null));
                await RefreshSoftwareAsync();
            },
            NavCleanup => Cleanup.LoadDrivesAsync,
            _ => null
        };
        if (load is null || !_requestedInventoryPages.Add(index))
            return;

        try
        {
            await load();
        }
        catch
        {
            _requestedInventoryPages.Remove(index);
            throw;
        }
    }

    public async Task OnSystemResumeAsync()
    {
        _nextBatteryTick = 0;
        if (_isWindowVisible)
            SampleVisiblePage();
        await RefreshThermalAsync(allowElevation: false);
        if (SelectedNavIndex == NavBattery)
            await RefreshGraphicsAsync(allowElevation: false);
    }

    public void SetWindowVisibility(bool visible)
    {
        if (_isWindowVisible == visible)
            return;
        _isWindowVisible = visible;
        if (_initialUiReady)
            UpdateSampling();
    }

    private void UpdateSampling()
    {
        _telemetryTimer.Stop();
        if (!_isWindowVisible || SelectedNavIndex != NavDashboard)
            _ = RunSafelyAsync(ReleaseIdleTelemetryAsync);
        if (_disposed || !_isWindowVisible || SelectedNavIndex is not (NavDashboard or NavBattery or NavProfiles))
            return;

        _nextBatteryTick = 0;
        _previousCpu = SystemMetricsInterop.SampleCpuTimes();
        SampleVisiblePage();
        _telemetryTimer.Start();
    }

    private async Task ReleaseIdleTelemetryAsync()
    {
        await _telemetryGate.WaitAsync(_lifetime.Token);
        try
        {
            if (!_isWindowVisible || SelectedNavIndex != NavDashboard)
                await Task.Run(_telemetry.ReleaseIdleResources);
        }
        finally
        {
            _telemetryGate.Release();
        }
    }

    private void SampleVisiblePage()
    {
        if (_disposed || !_isWindowVisible)
            return;
        if (SelectedNavIndex == NavDashboard)
        {
            ReadQuickMetrics();
            _ = RunSafelyAsync(() => RefreshTelemetryAsync(false));
        }

        if (SelectedNavIndex is NavDashboard or NavProfiles)
        {
            if (_isTargetLegion && !_isElevated && !_monitorAttempted)
                EnableFirmwareSensorsCommand.Execute(null);
            else if (_monitorRunning && _nextSensorSample.CurrentCount == 0)
                _nextSensorSample.Release();
        }

        if (!_batteryRefreshPending && Environment.TickCount64 >= _nextBatteryTick)
            _ = RunSafelyAsync(RefreshSlowStatusAsync);
    }

    private async Task RefreshSlowStatusAsync()
    {
        _batteryRefreshPending = true;
        _nextBatteryTick = Environment.TickCount64 + 10000;
        try
        {
            await Task.WhenAll(RefreshBatteryAsync(),
                _isElevated && SelectedNavIndex != NavBattery ? RefreshThermalAsync(false) : Task.CompletedTask);
        }
        finally
        {
            _batteryRefreshPending = false;
        }
    }

    private void ReadQuickMetrics()
    {
        var current = SystemMetricsInterop.SampleCpuTimes();
        if (current is not null && _previousCpu is not null &&
            SystemMetricsInterop.ComputeCpuUtilization(_previousCpu, current) is double cpu)
        {
            CpuUsagePercent = cpu;
            CpuUsageText = $"{cpu:F1} %";
            CpuHistory = Push(_cpuHistoryBuffer, cpu);
        }
        _previousCpu = current;

        if (SystemMetricsInterop.ReadMemoryUsage() is { } memory)
        {
            MemoryUsedPercent = memory.UsedPercent;
            MemoryShortText = $"{memory.UsedGb:F1} / {memory.TotalGb:F1} GB";
            MemoryUsageText = $"{MemoryShortText} ({memory.UsedPercent:F0}%)";
        }
        var ac = Win32BatteryInterop.IsAcOnlineQuick();
        LiveAcStateText = Loc.Get(ac ? "MsgAcOnline" : "MsgOnBattery");
        IsOnAcPower = ac;
        TelemetryTimestampText = DateTime.Now.ToString("HH:mm:ss");
        if (_monitorHasSample && Environment.TickCount64 - _lastSensorTick > 4500)
            CpuTempText = CpuFanRpmText = GpuFanRpmText = "--";
        if (_lastDetailTick != 0 && Environment.TickCount64 - _lastDetailTick > 4500)
        {
            GpuTempText = GpuPowerText = GpuUtilText = GpuVramText = CpuPowerText = DiskActivityText = "--";
            GpuUtilPercent = DiskActivePercent = double.NaN;
        }
    }

    private async Task RunSensorMonitorAsync()
    {
        _monitorAttempted = _monitorRunning = true;
        var ct = _lifetime.Token;
        try
        {
            var response = await Task.Run(() => new ElevatedWorkerClient().MonitorFirmwareSensorsAsync(
                (sample, thermal) => RunOnUiAsync(() =>
                {
                    if (_disposed)
                        return;
                    var firstSample = !_monitorHasSample;
                    _monitorHasSample = true;
                    _lastSensorTick = Environment.TickCount64;
                    CpuTempText = sample.CpuTemperatureC is int cpu ? $"{cpu} °C" : Loc.Get("LblUnavailable");
                    CpuFanRpmText = sample.CpuFanRpm is int fan ? $"{fan} RPM" : Loc.Get("LblUnavailable");
                    GpuFanRpmText = sample.GpuFanRpm is int gpuFan ? $"{gpuFan} RPM" : Loc.Get("LblUnavailable");
                    if (firstSample)
                        RaisePropertyChanged(nameof(FirmwareMonitorText));
                    if (thermal is not null && !IsHardwareBusy && thermal.CapturedAtUtc > _lastHardwareWriteUtc &&
                        (_latestThermalSnapshot?.CapturedAtUtc > thermal.CapturedAtUtc) != true)
                        ApplyThermalSnapshotToUi(thermal);
                }), token => _nextSensorSample.WaitAsync(token), ct), ct);
            if (!ct.IsCancellationRequested && !response.Success)
                StatusMessage = response.WasCancelled ? Loc.Get("MsgSensorsDenied") : Loc.Format("MsgSensorsFailed", response.Message ?? response.ErrorCode ?? string.Empty);
        }
        finally
        {
            _monitorRunning = _monitorHasSample = false;
            if (!_disposed)
            {
                CpuTempText = CpuFanRpmText = GpuFanRpmText = Loc.Get("LblNeedsAdmin");
                RaisePropertyChanged(nameof(FirmwareMonitorText));
            }
        }
    }

    private void SelectProfile(string profileId)
    {
        var profile = _settings.Profiles.FirstOrDefault(p => p.Id == profileId)
                      ?? ProfileSeedFactory.CreatePristineReference(ProfileSeedFactory.DailyId);
        SelectedProfileId = profile.Id;
        var reference = ProfileSeedFactory.CreatePristineReference(profile.Id);

        var draftMap = profile.Power.AsFieldMap();
        var refMap = reference.Power.AsFieldMap();
        var liveMap = _latestThermalSnapshot?.LivePowerValues?.AsFieldMap();

        ProfileFields.Clear();
        foreach (var contract in _settings.FieldContracts)
        {
            draftMap.TryGetValue(contract.FieldKey, out var draftVal);
            refMap.TryGetValue(contract.FieldKey, out var refVal);
            int? liveVal = null;
            liveMap?.TryGetValue(contract.FieldKey, out liveVal);

            ProfileFields.Add(new ProfileFieldRow
            {
                FieldKey = contract.FieldKey,
                DisplayLabel = Loc.Get($"Field{contract.FieldKey}"),
                Unit = contract.DisplayUnit,
                ReferenceValue = refVal ?? 0,
                DraftText = draftVal?.ToString() ?? string.Empty,
                LiveReadBackText = liveVal?.ToString() ?? "--",
                ContractStatusText = contract.IsVerifiedOnTarget ? Loc.Get("MsgVerifiedOnTarget") : Loc.Get("MsgPendingProbe")
            });
        }

        LoadFanPointsFromProfile(profile);
        _liveFanCapture = null;
        FanStages = (profile.CapturedLegionFanTable ?? LenovoFanCapture.OwnerReference).Stages;
        FanLevelText = string.Join(", ", FanStages.Select(stage => stage.SpeedLevel));
        FanCaptureStatus = Loc.Get(profile.CapturedLegionFanTable is null ? "FanCaptureReference" : "FanCaptureSaved");
        SaveFanCaptureCommand.RaiseCanExecuteChanged();
        ApplyFanLevelsCommand.RaiseCanExecuteChanged();
    }

    private void LoadFanPointsFromProfile(PerformanceProfile profile)
    {
        FanChannels.Clear();
        foreach (var channel in profile.FanCurve.Channels)
        {
            FanChannels.Add(FanChannelDraft.FromSaved(channel));
        }

        SelectedFanChannel = FanChannels.FirstOrDefault();
        FanStatusBannerText = profile.FanCurve.Status == FanCurveStatus.PendingCapture
            ? Loc.Get("MsgPendingFanCapture")
            : $"{profile.Id}: {Loc.Get("MsgFanDraftSaved")}";
        UpdateFanCurvePreview();
    }

    private ProfilePowerSettings ParseDraftSettingsFromRows()
    {
        int? ReadRow(string key)
        {
            var row = ProfileFields.FirstOrDefault(r => r.FieldKey == key);
            if (row is null || string.IsNullOrWhiteSpace(row.DraftText))
            {
                return null;
            }

            return int.TryParse(row.DraftText.Trim(), out var parsed) ? parsed : null;
        }

        return new ProfilePowerSettings
        {
            CpuLongTermPowerLimitW = ReadRow(nameof(ProfilePowerSettings.CpuLongTermPowerLimitW)),
            CpuCrossLoadingPowerLimitW = ReadRow(nameof(ProfilePowerSettings.CpuCrossLoadingPowerLimitW)),
            CpuLongTermPowerLimitTau = ReadRow(nameof(ProfilePowerSettings.CpuLongTermPowerLimitTau)),
            CpuShortTermPowerLimitW = ReadRow(nameof(ProfilePowerSettings.CpuShortTermPowerLimitW)),
            CpuThermalControlCircuitC = ReadRow(nameof(ProfilePowerSettings.CpuThermalControlCircuitC)),
            GpuDynamicBoostW = ReadRow(nameof(ProfilePowerSettings.GpuDynamicBoostW)),
            GpuToCpuDynamicBoostW = ReadRow(nameof(ProfilePowerSettings.GpuToCpuDynamicBoostW)),
            GpuConfigurableTgpW = ReadRow(nameof(ProfilePowerSettings.GpuConfigurableTgpW)),
            GpuTargetTemperatureLimitC = ReadRow(nameof(ProfilePowerSettings.GpuTargetTemperatureLimitC)),
            TotalProcessorPowerTargetAcW = ReadRow(nameof(ProfilePowerSettings.TotalProcessorPowerTargetAcW))
        };
    }

    private async Task SaveProfileDraftAsync()
    {
        var profileId = SelectedProfileId;
        var invalid = ProfileFields.FirstOrDefault(row =>
            !string.IsNullOrWhiteSpace(row.DraftText) && !int.TryParse(row.DraftText, out _));
        if (invalid is not null)
        {
            StatusMessage = Loc.Format("MsgInvalidPowerValue", invalid.DisplayLabel);
            return;
        }

        var updatedPower = ParseDraftSettingsFromRows();
        await UpdateSettingsAsync(settings => settings with
        {
            ActiveSelectedProfileId = profileId,
            Profiles = settings.Profiles.Select(p => p.Id == profileId
                ? p with { Power = updatedPower, LastModifiedUtc = DateTimeOffset.UtcNow }
                : p).ToArray()
        });
        StatusMessage = Loc.Format("MsgProfileDraftSaved", profileId);
    }

    private async Task ResetProfileToReferenceAsync()
    {
        var profileId = SelectedProfileId;
        var reference = ProfileSeedFactory.CreatePristineReference(profileId);
        await UpdateSettingsAsync(settings => settings with
        {
            Profiles = settings.Profiles.Select(p => p.Id == profileId
                ? p with { Power = reference.Power, LastModifiedUtc = DateTimeOffset.UtcNow }
                : p).ToArray()
        });
        if (SelectedProfileId == profileId)
        {
            foreach (var row in ProfileFields)
                row.DraftText = row.ReferenceValue.ToString();
        }

        StatusMessage = Loc.Format("MsgPowerDraftReset", profileId);
    }

    private async Task UpdateSettingsAsync(Func<AppSettingsState, AppSettingsState> update)
    {
        await _settingsSaveLock.WaitAsync();
        try
        {
            var updated = update(_settings);
            await _stateStore.SaveSettingsAsync(updated);
            _settings = updated;
        }
        finally
        {
            _settingsSaveLock.Release();
        }
    }

    private async Task RefreshThermalAsync(bool allowElevation)
    {
        var startedUtc = DateTimeOffset.UtcNow;
        var busyAtStart = IsHardwareBusy;
        var snap = await _hardware.ReadLiveThermalSnapshotAsync(allowElevation, _lifetime.Token).ConfigureAwait(false);
        await RunOnUiAsync(() =>
        {
            // Drop reads that overlapped a profile/fan write or are older than the shown snapshot;
            // applying them would revert the mode shown on the dashboard after a tray switch.
            if (busyAtStart || IsHardwareBusy || startedUtc <= _lastHardwareWriteUtc ||
                _latestThermalSnapshot?.CapturedAtUtc > snap.CapturedAtUtc)
                return;
            if (!snap.IsVerifiedLiveRead && _latestThermalSnapshot is { IsVerifiedLiveRead: true } latest)
            {
                LiveThermalNoteText = Loc.Format("MsgPowerLastRead", latest.CapturedAtUtc.LocalDateTime.ToString("HH:mm:ss"), snap.StatusNote);
                if (allowElevation)
                    StatusMessage = snap.StatusNote;
                return;
            }

            ApplyThermalSnapshotToUi(snap);
            if (allowElevation && !snap.IsVerifiedLiveRead)
                StatusMessage = snap.StatusNote;
        }).ConfigureAwait(false);
    }

    private async Task ApplyProfilePowerAsync(string profileId)
    {
        await RunQuickHardwareAsync(async () =>
        {
            var outcome = await _writeCoordinator.ApplyOwnerPowerPresetAsync(profileId);
            if (!outcome.Success || outcome.Value is null)
            {
                RaisePropertyChanged(nameof(ActiveProfileId));
                return DescribeQuickFailure(outcome);
            }

            SelectProfile(profileId);
            ApplyThermalSnapshotToUi(outcome.Value);
            return Loc.Format("MsgProfileApplied", SelectedProfileDisplayName);
        });
    }

    private async Task StartCoolingAsync()
    {
        using var stop = new CancellationTokenSource();
        _coolingStop = stop;
        StopCoolingCommand.RaiseCanExecuteChanged();
        StatusMessage = Loc.Get("MsgCoolingInProgress");
        try
        {
            await RunQuickHardwareAsync(async () =>
            {
                var outcome = await _writeCoordinator.RunCoolingCycleAsync(stop.Token);
                if (!outcome.Success || outcome.Value is not { } cycle)
                    return DescribeQuickFailure(outcome);
                var reason = Loc.Get($"CoolingStop{cycle.Reason}");
                static string FormatTemperature(int? value) => value is int degrees ? $"{degrees} °C" : "—";
                return Loc.Format("MsgCoolingComplete", reason,
                    FormatTemperature(cycle.Start.CpuC), FormatTemperature(cycle.End.CpuC),
                    FormatTemperature(cycle.Start.GpuC), FormatTemperature(cycle.End.GpuC));
            });
        }
        finally
        {
            _coolingStop = null;
            StopCoolingCommand.RaiseCanExecuteChanged();
        }
    }

    public void StopCooling()
    {
        _coolingStop?.Cancel();
        StopCoolingCommand.RaiseCanExecuteChanged();
    }

    private async Task RunQuickHardwareAsync(Func<Task<string>> action)
    {
        IsHardwareBusy = true;
        try
        {
            ReportQuickAction(await action());
        }
        catch (Exception ex)
        {
            ReportQuickAction(Loc.Format("MsgActionFailed", ex.Message));
        }
        finally
        {
            try
            {
                if (await _writeCoordinator.GetInterruptedOperationAsync() is { } interrupted)
                {
                    _interruptedOpId = interrupted.OperationId;
                    HasInterruptedOperation = true;
                    InterruptedOperationSummary = $"{interrupted.OperationType} ({interrupted.TargetDescription})";
                }
            }
            finally
            {
                _lastHardwareWriteUtc = DateTimeOffset.UtcNow;
                IsHardwareBusy = false;
            }
        }
    }

    private static string DescribeQuickFailure<T>(OperationResult<T> outcome) => outcome.ErrorCode switch
    {
        "USER_CANCELLED" => Loc.Get("MsgQuickCancelled"),
        "COOLING_REQUIRES_AC" => Loc.Get("MsgCoolingAcRequired"),
        "COOLING_UNAVAILABLE" or "COOLING_SENSOR_UNAVAILABLE" => Loc.Get("MsgCoolingUnavailable"),
        "COOLING_ALREADY_ACTIVE" => Loc.Get("MsgCoolingAlreadyActive"),
        _ => Loc.Format("MsgActionFailed", outcome.Message)
    };

    private void ReportQuickAction(string status)
    {
        StatusMessage = status;
        QuickActionCompleted?.Invoke(status);
    }

    private void ApplyThermalSnapshotToUi(LiveThermalSnapshot snap)
    {
        _latestThermalSnapshot = snap;
        RaisePropertyChanged(nameof(ActiveProfileId));
        LiveThermalModeText = snap.MatchedProfileId switch
        {
            ProfileSeedFactory.DailyId => Loc.Get("ProfileDailyName"),
            ProfileSeedFactory.GamingId => Loc.Get("ProfileGamingName"),
            _ => snap.IsVerifiedLiveRead ? snap.ThermalMode.ToString() : Loc.Get(!_isElevated && !_monitorHasSample ? "LblNeedsAdmin" : "LblUnavailable")
        };
        LiveThermalNoteText = snap.LivePowerValues is not null
            ? Loc.Format("MsgPowerLiveRead", snap.CapturedAtUtc.LocalDateTime.ToString("HH:mm:ss"))
            : snap.IsVerifiedLiveRead ? Loc.Get("MsgPowerCustomOnly") : snap.StatusNote;
        // Show only the active power plan's own (localized) name, e.g. "平衡".
        LiveWindowsPowerModeText = string.IsNullOrWhiteSpace(snap.ActivePowerSchemeName) ? "--" : snap.ActivePowerSchemeName;
        LiveAcStateText = snap.IsOnAcPower ? Loc.Get("MsgAcOnline") : Loc.Get("MsgOnBattery");
        IsOnAcPower = snap.IsOnAcPower;

        var liveMap = snap.LivePowerValues?.AsFieldMap();
        foreach (var row in ProfileFields)
        {
            if (liveMap is not null && liveMap.TryGetValue(row.FieldKey, out var val) && val is not null)
            {
                row.LiveReadBackText = val.Value.ToString();
            }
            else
            {
                row.LiveReadBackText = "--";
            }
        }

    }

    private async Task RefreshBatteryAsync()
    {
        var snap = await _battery.ReadStatusAsync(_lifetime.Token).ConfigureAwait(false);
        await RunOnUiAsync(() =>
        {
            if (_batterySnapshot is { } current && current.CapturedAtUtc > snap.CapturedAtUtc)
                return;
            _batterySnapshot = snap;
            RaisePropertyChanged(nameof(CanSetConservation));
            SetConservationCommand.RaiseCanExecuteChanged();
            BatteryChargeText = snap.ChargePercent is not null ? $"{snap.ChargePercent}%" : Loc.Get("LblUnavailable");
            BatteryChargePercent = snap.ChargePercent is { } pct ? pct : double.NaN;
            IsOnAcPower = snap.IsAcOnline;
            BatteryActivityText = snap.Activity switch
            {
                BatteryChargeActivity.Charging => Loc.Get("MsgBatteryCharging"),
                BatteryChargeActivity.Critical => Loc.Get("MsgBatteryCritical"),
                BatteryChargeActivity.Discharging => Loc.Get("MsgOnBattery"),
                BatteryChargeActivity.IdleOnAc => Loc.Get("MsgAcOnline"),
                _ => Loc.Get(snap.IsAcOnline ? "MsgAcOnline" : "MsgOnBattery")
            };
            BatteryHealthText = snap.HealthPercent is not null ? $"{snap.HealthPercent:F2}%" : Loc.Get("LblUnavailable");
            BatteryDesignCapText = snap.DesignCapacityMwh is not null ? $"{snap.DesignCapacityMwh:N0} mWh" : Loc.Get("LblUnavailable");
            BatteryFullCapText = snap.FullChargeCapacityMwh is not null ? $"{snap.FullChargeCapacityMwh:N0} mWh" : Loc.Get("LblUnavailable");
            BatteryCycleText = snap.CycleCount is not null ? snap.CycleCount.Value.ToString() : Loc.Get("LblUnavailable");
            BatteryConservationText = snap.LenovoChargeMode switch
            {
                BatteryChargeMode.Conservation => Loc.Get("MsgConservationOn"),
                BatteryChargeMode.RapidCharge => Loc.Get("MsgRapidCharge"),
                BatteryChargeMode.Normal => string.Empty,
                _ => Loc.Get("LblUnavailable")
            };
            BatteryRawDetailText = snap.ProviderNote;
            RaisePropertyChanged(nameof(BatteryConservationEnabled));
        }).ConfigureAwait(false);
    }

    public bool CanSetConservation => _batterySnapshot?.IsConservationWriteSupported == true;

    /// <summary>Two-way state for the conservation toggle; setting it runs the firmware write, then re-reads.</summary>
    public bool BatteryConservationEnabled
    {
        get => _batterySnapshot?.LenovoChargeMode == BatteryChargeMode.Conservation;
        set
        {
            var parameter = value ? "on" : "off";
            if (value != BatteryConservationEnabled && SetConservationCommand.CanExecute(parameter))
                SetConservationCommand.Execute(parameter);
            else
                RaisePropertyChanged(); // snap the toggle back to the real state
        }
    }
    private bool BatteryIsConservationOff => _batterySnapshot?.LenovoChargeMode != BatteryChargeMode.Conservation;

    private Task SetConservationAsync(bool enabled) => RunQuickHardwareAsync(async () =>
    {
        var outcome = await _battery.SetConservationModeAsync(enabled, _lifetime.Token);
        await RefreshBatteryAsync();
        return outcome.Success
            ? Loc.Get(enabled ? "MsgConservationOn" : "MsgConservationDisabled")
            : DescribeQuickFailure(outcome);
    });

    private async Task RefreshTelemetryAsync(bool allowWakeDgpu)
    {
        if (_disposed)
            return;

        var ct = _lifetime.Token;
        if (allowWakeDgpu)
            await _telemetryGate.WaitAsync(ct);
        else if (!await _telemetryGate.WaitAsync(0, ct))
            return;

        try
        {
            var allowGpuQuery = allowWakeDgpu || !PauseGpuOnBattery;
            var graphicsMode = _settings.PendingGraphicsMode ?? _graphicsSnapshot?.PendingMode ??
                               _graphicsSnapshot?.EffectiveMode ?? _settings.LastVerifiedGraphicsMode;
            var pauseDgpu = graphicsMode == GraphicsRoutingMode.HybridIgpuOnly ||
                _graphicsSnapshot is { IsVerifiedLiveRead: true, EffectiveMode: GraphicsRoutingMode.HybridIgpuOnly };
            var snap = await Task.Run(() => _telemetry.ReadSnapshotAsync(allowGpuQuery, ct, pauseDgpu), ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested)
                return;

            await RunOnUiAsync(() =>
            {
                _lastDetailTick = Environment.TickCount64;
                CpuPowerText = snap.CpuPowerWatts is { } cpuPower ? FormatReading(cpuPower, "F1") : "--";
                if (!_monitorHasSample)
                {
                    CpuTempText = FormatReading(snap.CpuTemperature, "F0");
                    CpuFanRpmText = FormatReading(snap.CpuFanRpm, "D");
                    GpuFanRpmText = FormatReading(snap.GpuFanRpm, "D");
                }
                DiskActivityText = snap.DiskActivePercent.Status == SensorStatus.Valid
                    ? $"{snap.DiskActivePercent.Value:F1}% | {snap.DiskThroughputMbPerSec.Value:F2} MB/s"
                    : "--";

                if (!DiskCapacities.SequenceEqual(snap.DiskCapacities))
                {
                    DiskCapacities.Clear();
                    foreach (var disk in snap.DiskCapacities)
                        DiskCapacities.Add(disk);
                }

                GpuNameText = snap.GpuName ?? "NVIDIA RTX (dGPU)";
                GpuUtilText = FormatReading(snap.GpuUtilization, "F0");
                GpuTempText = FormatReading(snap.GpuTemperature, "F0");
                GpuPowerText = FormatReading(snap.GpuPowerWatts, "F1");
                GpuVramText = snap.GpuMemoryUsedMb.Status == SensorStatus.Valid && snap.GpuMemoryTotalMb.Status == SensorStatus.Valid
                    ? $"{snap.GpuMemoryUsedMb.Value:F0} / {snap.GpuMemoryTotalMb.Value:F0} MB"
                    : FormatReading(snap.GpuMemoryUsedMb, "F0");
                GpuPstateText = snap.GpuPowerState ?? "--";
                GpuUtilPercent = ValueOrNaN(snap.GpuUtilization);
                DiskActivePercent = ValueOrNaN(snap.DiskActivePercent);
                GpuHistory = Push(_gpuHistoryBuffer, GpuUtilPercent);
            }).ConfigureAwait(false);
        }
        finally
        {
            _telemetryGate.Release();
        }
    }

    private static double ValueOrNaN(SensorReading<double> reading) =>
        reading.Status == SensorStatus.Valid && reading.Value is { } v ? v : double.NaN;

    private static IReadOnlyList<double> Push(Queue<double> buffer, double value)
    {
        if (buffer.Count == 0 && !double.IsFinite(value))
            return [];

        buffer.Enqueue(value);
        while (buffer.Count > HistoryCapacity)
            buffer.Dequeue();
        return buffer.ToArray();
    }

    private async Task RefreshAllAsync()
    {
        ReadQuickMetrics();
        await Task.WhenAll(RefreshThermalAsync(false), RefreshBatteryAsync(), RefreshTelemetryAsync(true));
        await RunOnUiAsync(() => StatusMessage = Loc.Format("MsgRefreshedAt", DateTime.Now.ToString("HH:mm:ss"))).ConfigureAwait(false);
    }

    private static string DisplayUnit(string unit) => unit == "C" ? "°C" : unit;

    private static string FormatReading<T>(SensorReading<T> reading, string format) where T : struct =>
        reading.Status switch
        {
            SensorStatus.Valid when reading.Value is double d => $"{d.ToString(format)} {DisplayUnit(reading.Unit)}",
            SensorStatus.Valid when reading.Value is int i => $"{i} {DisplayUnit(reading.Unit)}",
            SensorStatus.PausedOnBattery => Loc.Get("MsgPausedOnBattery"),
            SensorStatus.PausedInIntegratedMode => Loc.Get("MsgPausedInIntegratedMode"),
            SensorStatus.RequiresElevation => Loc.Get("LblNeedsAdmin"),
            SensorStatus.Stale or SensorStatus.Sleeping => "--",
            _ => Loc.Get("LblUnavailable")
        };

    private async Task RefreshGraphicsAsync(bool allowElevation)
    {
        var snap = await _hardware.ReadGraphicsStatusAsync(allowElevation, _lifetime.Token).ConfigureAwait(false);
        await RunOnUiAsync(() =>
        {
            if (!allowElevation && snap.StatusNote == "GRAPHICS_REQUIRES_ADMIN" && _graphicsSnapshot is not null)
                return;
            SetGraphicsSnapshot(snap);
        }).ConfigureAwait(false);
    }

    /// <summary>The two user-facing choices: 独显 (DgpuOnly) and 核显 (HybridIgpuOnly).</summary>
    private static readonly GraphicsRoutingMode[] SwitchableGraphicsModes = [GraphicsRoutingMode.DgpuOnly, GraphicsRoutingMode.HybridIgpuOnly];

    private GraphicsRoutingMode? CurrentGraphicsMode =>
        _graphicsSnapshot is { IsVerifiedLiveRead: true } snap ? snap.PendingMode ?? snap.EffectiveMode : null;

    private static string GraphicsOptionName(GraphicsRoutingMode mode) => mode switch
    {
        GraphicsRoutingMode.DgpuOnly => Loc.Get("GraphicsOptionDgpu"),
        GraphicsRoutingMode.HybridIgpuOnly => Loc.Get("GraphicsOptionHybrid"),
        _ => Loc.Get($"GraphicsMode{mode}")
    };

    /// <summary>
    /// Choices shown in the picker: 独显 / 核显 minus the mode already in use. When the current mode is
    /// not known yet (standard user), both are offered; switching reads it with elevation first.
    /// </summary>
    public IReadOnlyList<GraphicsModeOption> GraphicsModes
    {
        get
        {
            var supported = _graphicsSnapshot is { IsVerifiedLiveRead: true } snap ? snap.SupportedModes : null;
            var current = CurrentGraphicsMode;
            return SwitchableGraphicsModes
                .Where(mode => mode != current && (supported is null || supported.Any(o => o.Mode == mode)))
                .Select(mode => new GraphicsModeOption(mode, true, true, true, GraphicsOptionName(mode)))
                .ToArray();
        }
    }

    public bool HasGraphicsModes => GraphicsModes.Count > 0;

    public GraphicsModeOption? SelectedGraphicsMode
    {
        get => _selectedGraphicsMode;
        set
        {
            if (SetField(ref _selectedGraphicsMode, value))
                ApplyGraphicsModeCommand.RaiseCanExecuteChanged();
        }
    }

    private void SetGraphicsSnapshot(GraphicsStatusSnapshot snap)
    {
        if (_settings.PendingGraphicsMode is { } pending &&
            _settings.PendingGraphicsBootSession is { } submittedBoot)
        {
            if (!snap.IsVerifiedLiveRead || snap.BootSession is null)
            {
                snap = snap with { PendingMode = pending, RebootRequired = true };
            }
            else if (submittedBoot == snap.BootSession && pending != snap.EffectiveMode)
            {
                snap = snap with { PendingMode = pending, RebootRequired = true };
            }
            else if (snap.BootSession is not null && submittedBoot != snap.BootSession)
            {
                if (pending != snap.EffectiveMode && pending != snap.PendingMode)
                    snap = snap with { StatusNote = Loc.Format("MsgGraphicsBootMismatch", Loc.Get($"GraphicsMode{pending}")) };
                _ = RunSafelyAsync(() => UpdateSettingsAsync(settings =>
                    settings.PendingGraphicsBootSession != submittedBoot || settings.PendingGraphicsMode != pending
                        ? settings
                        : settings with
                        {
                            PendingGraphicsMode = null,
                            PendingGraphicsBootSession = null,
                            LastVerifiedGraphicsMode = snap.EffectiveMode
                        }));
            }
        }

        _graphicsSnapshot = snap with
        {
            SupportedModes = snap.SupportedModes.Select(option => option with
            {
                Description = Loc.Get($"GraphicsMode{option.Mode}")
            }).ToArray()
        };
        EffectiveGraphicsText = snap.IsVerifiedLiveRead ? Loc.Get($"GraphicsMode{snap.EffectiveMode}") : Loc.Get("LblUnavailable");
        PendingGraphicsText = snap.PendingMode is { } displayedPending ? Loc.Get($"GraphicsMode{displayedPending}") : "—";
        HasPendingGraphicsReboot = snap.RebootRequired;
        GraphicsNoteText = snap.StatusNote switch
        {
            "GRAPHICS_REQUIRES_ADMIN" => string.Empty,
            "GRAPHICS_UNSUPPORTED" => Loc.Get("MsgGraphicsUnsupported"),
            "GRAPHICS_UNAVAILABLE" => Loc.Get("LblUnavailable"),
            "USER_CANCELLED" => Loc.Get("MsgSensorsDenied"),
            _ => snap.StatusNote
        };
        RaisePropertyChanged(nameof(GraphicsModes));
        RaisePropertyChanged(nameof(HasGraphicsModes));
        SelectedGraphicsMode = GraphicsModes.FirstOrDefault();
        ApplyGraphicsModeCommand.RaiseCanExecuteChanged();
    }

    private async Task ApplyGraphicsModeAsync()
    {
        if (SelectedGraphicsMode is not { } option)
            return;
        var modeName = GraphicsOptionName(option.Mode);

        // Standard users cannot read the current mode; read it with elevation now (one UAC prompt).
        if (_graphicsSnapshot?.IsVerifiedLiveRead != true)
        {
            await RefreshGraphicsAsync(allowElevation: true);
            if (_graphicsSnapshot?.IsVerifiedLiveRead != true)
                return;
            if (option.Mode == CurrentGraphicsMode)
            {
                StatusMessage = Loc.Format("MsgGraphicsAlreadyActive", modeName);
                return;
            }
        }

        if (MessageBox.Show(Application.Current.MainWindow, Loc.Format("MsgGraphicsConfirm", modeName),
                Loc.Get("GraphicsHeader"), MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        await RunQuickHardwareAsync(async () =>
        {
            var outcome = await _hardware.SetGraphicsModeAsync(option.Mode, _lifetime.Token);
            if (!outcome.Success || outcome.Value is not { } updated)
            {
                if (outcome.ErrorCode is "ROLLED_BACK" or "RESTORE_UNVERIFIED")
                {
                    await UpdateSettingsAsync(settings => settings with { PendingGraphicsMode = null, PendingGraphicsBootSession = null });
                    SetGraphicsSnapshot(new GraphicsStatusSnapshot(GraphicsRoutingMode.Unknown, null, false, [], false,
                        Loc.Get("MsgGraphicsAuthorize"), DateTimeOffset.UtcNow));
                }
                return DescribeQuickFailure(outcome);
            }

            await UpdateSettingsAsync(settings => settings with
            {
                PendingGraphicsMode = updated.RebootRequired ? updated.PendingMode : null,
                PendingGraphicsBootSession = updated.RebootRequired ? updated.BootSession : null,
                LastVerifiedGraphicsMode = updated.IsVerifiedLiveRead ? updated.EffectiveMode : settings.LastVerifiedGraphicsMode
            });
            SetGraphicsSnapshot(updated);
            return Loc.Format(updated.PendingMode is null ? "MsgGraphicsApplied" :
                updated.RebootRequired ? "MsgGraphicsQueued" : "MsgGraphicsWaitingForWindows", modeName);
        });
    }

    private async Task RefreshSoftwareAsync()
    {
        var entries = await _software.ReadStartupEntriesAsync(_lifetime.Token).ConfigureAwait(false);
        await RunOnUiAsync(() =>
        {
            _allStartupEntries = entries.Select(entry => new StartupEntryRow(entry, SetStartupEntryEnabledAsync)).ToList();
            FilterStartupEntries();
        });
    }

    /// <summary>Applies a startup switch flip; returning false makes the row's switch snap back.</summary>
    private async Task<bool> SetStartupEntryEnabledAsync(StartupEntryRow row, bool enabled)
    {
        // Windows' own tasks (Task Scheduler folder \Microsoft\…) keep the system healthy; ask before turning one off.
        if (!enabled && row.IsSystemTask && MessageBox.Show(Application.Current.MainWindow,
                Loc.Format("MsgStartupSystemTaskConfirm", row.Name), Loc.Get("TabStartupEntries"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return false;

        var result = await _software.SetStartupEntryEnabledAsync(row.Entry, enabled, _lifetime.Token);
        StatusMessage = result.Success
            ? Loc.Format(enabled ? "MsgStartupEnabled" : "MsgStartupDisabled", row.Name)
            : result.WasCancelledByUser
                ? Loc.Format("MsgStartupCancelled", row.Name)
                : Loc.Format("MsgStartupFailed", row.Name, result.Message);
        return result.Success;
    }

    private void AddFanChannel()
    {
        var id = FanChannels.Count == 0 ? 1 : FanChannels.Max(channel => channel.Id) + 1;
        var channel = new FanChannelDraft { Id = id, Name = Loc.Format("FanChannelName", id) };
        FanChannels.Add(channel);
        SelectedFanChannel = channel;
    }

    private void AddFanPoint()
    {
        if (SelectedFanChannel is null)
            AddFanChannel();

        SelectedFanChannel!.Points.Add(new FanPointRow());
        UpdateFanCurvePreview();
    }

    private void UpdateFanCurvePreview()
    {
        var points = new PointCollection();
        var channel = SelectedFanChannel?.ToDraftCurve();
        if (channel is null || channel.Points.Count == 0)
        {
            FanPreviewPoints = points;
            FanValidationText = string.Empty; // the page banner already explains the pending capture
            return;
        }

        const double width = 420, height = 180;
        var minTemperature = Math.Min(30, channel.Points.Min(point => point.TemperatureC));
        var maxTemperature = Math.Max(100, channel.Points.Max(point => point.TemperatureC));
        var maxSpeed = Math.Max(1, channel.SpeedUnit == "%" ? 100 : channel.Points.Max(point => point.FanSpeedValue));
        foreach (var point in channel.Points)
        {
            var x = (point.TemperatureC - (double)minTemperature) / (maxTemperature - (double)minTemperature) * width;
            var y = height - Math.Clamp(point.FanSpeedValue / (double)maxSpeed, 0, 1) * height;
            points.Add(new Point(x, y));
        }

        FanPreviewPoints = points;
        var result = _validator.ValidateFanCurve(new FanCurveProfile(FanCurveStatus.Captured, "Manual draft", [channel]));
        FanValidationText = result.IsValid ? Loc.Get("MsgFanDraftGeometryValid") : string.Join(" | ", result.Errors);
    }

    private async Task SaveFanDraftAsync()
    {
        var profileId = SelectedProfileId;
        var channels = FanChannels.Select(channel => channel.ToDraftCurve()).ToArray();
        var curve = channels.Length == 0
            ? new FanCurveProfile(FanCurveStatus.PendingCapture, "No firmware fan table saved for this profile", [])
            : new FanCurveProfile(FanCurveStatus.Captured, "Manual capture. Firmware bounds and identity not verified.", channels);

        if (channels.Length > 0)
        {
            var result = _validator.ValidateFanCurve(curve);
            if (!result.IsValid)
            {
                FanValidationText = string.Join(" | ", result.Errors);
                return;
            }
        }

        await UpdateSettingsAsync(settings => settings with
        {
            ActiveSelectedProfileId = profileId,
            Profiles = settings.Profiles.Select(profile => profile.Id == profileId
                ? profile with { FanCurve = curve, LastModifiedUtc = DateTimeOffset.UtcNow }
                : profile).ToArray()
        });
        if (SelectedProfileId == profileId)
        {
            FanStatusBannerText = channels.Length == 0
                ? Loc.Get("MsgPendingFanCapture")
                : $"{profileId}: {Loc.Get("MsgFanDraftSaved")}";
        }

        StatusMessage = Loc.Get("MsgFanDraftSaved");
    }

    private async Task ReadFanFromFirmwareAsync()
    {
        var profileId = SelectedProfileId;
        var res = await _hardware.ReadFanCurveAsync(allowElevation: true);
        StatusMessage = res.Message;
        if (SelectedProfileId != profileId)
            return;

        _liveFanCapture = res.Success ? res.Value?.LiveCapture : null;
        SaveFanCaptureCommand.RaiseCanExecuteChanged();
        ApplyFanLevelsCommand.RaiseCanExecuteChanged();
        if (_liveFanCapture is not null)
        {
            FanStages = _liveFanCapture.Stages;
            FanLevelText = string.Join(", ", FanStages.Select(stage => stage.SpeedLevel));
            FanCaptureStatus = Loc.Get("FanCaptureLive");
        }
        else
        {
            var saved = _settings.Profiles.FirstOrDefault(profile => profile.Id == profileId)?.CapturedLegionFanTable;
            FanStages = (saved ?? LenovoFanCapture.OwnerReference).Stages;
            FanCaptureStatus = Loc.Get(saved is null ? "FanCaptureReference" : "FanCaptureSaved");
        }

        FirmwareFanTableText = res.Value is { } curve
            ? string.Join(Environment.NewLine, curve.Channels.Select(channel =>
                $"{channel.ChannelName} / {channel.SensorName}: " +
                string.Join(", ", channel.Points.Select(point => $"{point.TemperatureC}={point.FanSpeedValue}"))))
            : string.Empty;
    }

    private async Task ApplyFanLevelsAsync(bool restoreReference)
    {
        var levels = restoreReference
            ? LenovoFanCapture.OwnerReference.Stages.Select(stage => stage.SpeedLevel).ToArray()
            : FanLevelText.Split([',', '，', ' '], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(value => int.TryParse(value, out var level) ? level : -1).ToArray();
        if (!LenovoFanCapture.AreWritableLevels(levels))
        {
            StatusMessage = Loc.Get("MsgFanInvalidLevels");
            return;
        }

        if (restoreReference && MessageBox.Show(Application.Current.MainWindow, Loc.Get("MsgFanRestoreReference"),
                Loc.Get("FanHeader"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        await RunQuickHardwareAsync(async () =>
        {
            var outcome = await _hardware.SetFanSpeedLevelsAsync(levels, _lifetime.Token);
            if (!outcome.Success || outcome.Value?.LiveCapture is not { } capture)
            {
                if (outcome.ErrorCode == "RESTORE_UNVERIFIED")
                {
                    _liveFanCapture = null;
                    FanCaptureStatus = Loc.Get("MsgFanReadAgain");
                    SaveFanCaptureCommand.RaiseCanExecuteChanged();
                    ApplyFanLevelsCommand.RaiseCanExecuteChanged();
                }
                return DescribeQuickFailure(outcome);
            }
            _liveFanCapture = capture;
            FanStages = capture.Stages;
            FanLevelText = string.Join(", ", levels);
            FanCaptureStatus = Loc.Get("FanCaptureLive");
            SaveFanCaptureCommand.RaiseCanExecuteChanged();
            return Loc.Get("MsgFanLevelsApplied");
        });
    }

    private async Task SaveFanCaptureAsync()
    {
        var profileId = SelectedProfileId;
        var capture = _liveFanCapture;
        if (capture is null)
            return;

        await UpdateSettingsAsync(settings => settings with
        {
            Profiles = settings.Profiles.Select(profile => profile.Id == profileId
                ? profile with { CapturedLegionFanTable = capture, LastModifiedUtc = DateTimeOffset.UtcNow }
                : profile).ToArray()
        });
        if (SelectedProfileId == profileId && ReferenceEquals(_liveFanCapture, capture))
        {
            _liveFanCapture = null;
            FanCaptureStatus = Loc.Get("FanCaptureSaved");
            SaveFanCaptureCommand.RaiseCanExecuteChanged();
            ApplyFanLevelsCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task DismissInterruptedAsync()
    {
        if (_interruptedOpId is not null)
        {
            await _writeCoordinator.DismissInterruptedOperationAsync(_interruptedOpId, "Reviewed by user on startup.").ConfigureAwait(false);
        }

        await RunOnUiAsync(() =>
        {
            HasInterruptedOperation = false;
            InterruptedOperationSummary = string.Empty;
        }).ConfigureAwait(false);
    }

    private static Task RunOnUiAsync(Action action)
    {
        var disp = Application.Current?.Dispatcher;
        if (disp is null || disp.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return disp.InvokeAsync(action).Task;
    }

    public async Task RunSafelyAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (Exception ex)
        {
            if (!_disposed)
                await RunOnUiAsync(() => StatusMessage = Loc.Format("MsgActionFailed", ex.Message));
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        StopCooling();
        Cleanup.Dispose();
        if (_uninstall.IsValueCreated)
            Uninstall.CancelScan();
        _lifetime.Cancel();
        _telemetryTimer.Stop();
    }
}
