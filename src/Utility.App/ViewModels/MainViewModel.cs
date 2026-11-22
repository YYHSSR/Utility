using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Utility.App.Localization;
using Utility.Core.Models;
using Utility.Core.Services;
using Utility.Platform.Lenovo;

namespace Utility.App.ViewModels;

/// <summary>Main coordinator ViewModel backing Utility's single-window navigation and 7 features.</summary>
public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private static readonly JsonSerializerOptions ReportJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly JsonStateStore _stateStore;
    private readonly ProfileValidationService _validator;
    private readonly IBatteryProvider _battery;
    private readonly ITelemetryProvider _telemetry;
    private readonly ISoftwareInventoryProvider _software;
    private readonly IDriverProvider _drivers;
    private readonly ILenovoHardwareControlProvider _hardware;
    private readonly HardwareWriteCoordinator _writeCoordinator;
    private readonly System.Timers.Timer _telemetryTimer;

    private AppSettingsState _settings = new();
    private CapabilityReport? _latestCapabilityReport;
    private LiveThermalSnapshot? _latestThermalSnapshot;
    private IReadOnlyList<InstalledAppEntry> _allApps = [];
    private IReadOnlyList<InstalledDriverEntry> _allDrivers = [];
    private DriverPackageInspection? _inspectedPackage;
    private DriverPackageDecision? _inspectedDecision;
    private string? _interruptedOpId;
    private bool _isWindowVisible = true;

    private int _selectedNavIndex;
    private string _machineBannerText = "LENOVO 83JJ";
    private string _privilegeBadgeText = Loc.Get("StatusStandardUser");
    private string _statusMessage = string.Empty;
    private bool _hasInterruptedOperation;
    private string _interruptedOperationSummary = string.Empty;

    // F1 Profiles
    private string _selectedProfileId = ProfileSeedFactory.DailyId;
    private string _liveThermalModeText = "--";
    private string _liveWindowsPowerModeText = "--";
    private string _liveAcStateText = "--";
    private string _profileBlockingNote = string.Empty;
    private bool _canApplyFullProfile;
    private bool _canApplyPowerOnly;

    // F2 Battery
    private string _batteryChargeText = "--";
    private string _batteryActivityText = "--";
    private string _batteryHealthText = "--";
    private string _batteryDesignCapText = "--";
    private string _batteryFullCapText = "--";
    private string _batteryCycleText = "--";
    private string _batteryConservationText = "--";
    private string _batteryRawDetailText = string.Empty;
    private bool _isConservationSupported;
    private bool _isConservationEnabled;

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
    private string _gpuVramText = "--";
    private string _gpuPstateText = "--";
    private string _cpuFanRpmText = "--";
    private string _gpuFanRpmText = "--";
    private string _telemetryTimestampText = "--";

    // F4 Graphics
    private string _effectiveGraphicsText = "--";
    private string _pendingGraphicsText = "--";
    private bool _hasPendingGraphicsReboot;
    private GraphicsRoutingMode _selectedTargetGraphicsMode = GraphicsRoutingMode.Hybrid;
    private bool _confirmGraphicsRecoveryPath;
    private string _graphicsNoteText = string.Empty;

    // F5 Software
    private string _appSearchText = string.Empty;
    private InstalledAppEntry? _selectedInstalledApp;
    private string _softwareSummaryText = string.Empty;

    // F6 Fan curves
    private string _fanStatusBannerText = Loc.Get("MsgPendingFanCapture");
    private string _fanValidationText = string.Empty;
    private PointCollection _fanPreviewPoints = [];

    // F7 Drivers
    private DriverCategory _selectedDriverCategory = DriverCategory.All;
    private string _driverPackagePathText = string.Empty;
    private string _driverPackageVerdictText = string.Empty;
    private string _driverPackageSha256Text = string.Empty;
    private bool _canLaunchVerifiedPackage;
    private bool _userConfirmedPackageLaunch;

    public MainViewModel(
        JsonStateStore stateStore,
        ProfileValidationService validator,
        IBatteryProvider battery,
        ITelemetryProvider telemetry,
        ISoftwareInventoryProvider software,
        IDriverProvider drivers,
        ILenovoHardwareControlProvider hardware,
        HardwareWriteCoordinator writeCoordinator)
    {
        _stateStore = stateStore;
        _validator = validator;
        _battery = battery;
        _telemetry = telemetry;
        _software = software;
        _drivers = drivers;
        _hardware = hardware;
        _writeCoordinator = writeCoordinator;

        NavigateCommand = new RelayCommand(p => { if (p is string s && int.TryParse(s, out var idx)) SelectedNavIndex = idx; });
        DismissInterruptedOperationCommand = new AsyncRelayCommand(DismissInterruptedAsync);

        SelectProfileCommand = new RelayCommand(p => { if (p is string id) SelectProfile(id); });
        SaveProfileDraftCommand = new AsyncRelayCommand(SaveProfileDraftAsync);
        ResetProfileToReferenceCommand = new AsyncRelayCommand(ResetProfileToReferenceAsync);
        RefreshLiveThermalCommand = new AsyncRelayCommand(() => RefreshThermalAsync(allowElevation: true));
        ApplyFullProfileCommand = new AsyncRelayCommand(ApplyFullProfileAsync, () => CanApplyFullProfile);
        ApplyPowerFieldsOnlyCommand = new AsyncRelayCommand(ApplyPowerFieldsOnlyAsync, () => CanApplyPowerOnly);
        SetStockThermalModeCommand = new AsyncRelayCommand(SetStockThermalModeAsync);

        RefreshBatteryCommand = new AsyncRelayCommand(RefreshBatteryAsync);
        ToggleConservationCommand = new AsyncRelayCommand(ToggleConservationAsync, () => _isConservationSupported);

        ManualRefreshTelemetryCommand = new AsyncRelayCommand(() => RefreshTelemetryAsync(allowWakeDgpu: true));

        RefreshGraphicsCommand = new AsyncRelayCommand(() => RefreshGraphicsAsync(allowElevation: true));
        ApplyGraphicsModeCommand = new AsyncRelayCommand(ApplyGraphicsModeAsync, () => ConfirmGraphicsRecoveryPath);

        OpenWindowsAppsCommand = new RelayCommand(() => _software.OpenWindowsInstalledApps(SelectedInstalledApp?.PackageFamilyName));
        OpenWindowsStartupCommand = new RelayCommand(() => _software.OpenWindowsStartupApps());
        RunLeftoverScanCommand = new AsyncRelayCommand(() => RefreshSoftwareAsync(includeLeftovers: true));

        AddFanPointCommand = new RelayCommand(AddFanPoint);
        ValidateFanPreviewCommand = new RelayCommand(UpdateFanCurvePreview);
        ReadFanTableCommand = new AsyncRelayCommand(ReadFanFromFirmwareAsync);
        ApplyFanCurveCommand = new AsyncRelayCommand(ApplyFanCurveAsync);
        RestoreDefaultFanCommand = new AsyncRelayCommand(RestoreDefaultFanAsync);

        OpenExternalDriverLinkCommand = new RelayCommand(p => { if (p is string url) _drivers.OpenExternalTarget(url); });
        BrowseDriverPackageCommand = new AsyncRelayCommand(BrowseAndInspectDriverPackageAsync);
        LaunchVerifiedPackageCommand = new AsyncRelayCommand(LaunchVerifiedDriverPackageAsync, () => CanLaunchVerifiedPackage && UserConfirmedPackageLaunch);

        RunElevatedProbeCommand = new AsyncRelayCommand(() => RefreshCapabilitiesAsync(allowElevation: true));
        ExportCapabilityReportCommand = new AsyncRelayCommand(ExportCapabilityReportAsync);

        _telemetryTimer = new System.Timers.Timer(2000) { AutoReset = true };
        _telemetryTimer.Elapsed += async (_, _) =>
        {
            if (_isWindowVisible && SelectedNavIndex == 2)
            {
                await RefreshTelemetryAsync(allowWakeDgpu: false).ConfigureAwait(false);
            }
        };
    }

    public ObservableCollection<ProfileFieldRow> ProfileFields { get; } = [];
    public ObservableCollection<DiskCapacityInfo> DiskCapacities { get; } = [];
    public ObservableCollection<GraphicsModeOption> SupportedGraphicsModes { get; } = [];
    public ObservableCollection<InstalledAppEntry> FilteredApps { get; } = [];
    public ObservableCollection<StartupEntry> StartupEntries { get; } = [];
    public ObservableCollection<LenovoLeftoverFinding> LeftoverFindings { get; } = [];
    public ObservableCollection<FanPointRow> FanPoints { get; } = [];
    public ObservableCollection<InstalledDriverEntry> FilteredDrivers { get; } = [];
    public ObservableCollection<FeatureCapability> Capabilities { get; } = [];
    public ObservableCollection<string> UnresolvedItems { get; } = [];

    public IReadOnlyList<DriverCategory> DriverCategories { get; } = Enum.GetValues<DriverCategory>();
    public OfficialDriverLinks DriverLinks => OfficialDriverLinks.Default;

    public RelayCommand NavigateCommand { get; }
    public AsyncRelayCommand DismissInterruptedOperationCommand { get; }
    public RelayCommand SelectProfileCommand { get; }
    public AsyncRelayCommand SaveProfileDraftCommand { get; }
    public AsyncRelayCommand ResetProfileToReferenceCommand { get; }
    public AsyncRelayCommand RefreshLiveThermalCommand { get; }
    public AsyncRelayCommand ApplyFullProfileCommand { get; }
    public AsyncRelayCommand ApplyPowerFieldsOnlyCommand { get; }
    public AsyncRelayCommand SetStockThermalModeCommand { get; }
    public AsyncRelayCommand RefreshBatteryCommand { get; }
    public AsyncRelayCommand ToggleConservationCommand { get; }
    public AsyncRelayCommand ManualRefreshTelemetryCommand { get; }
    public AsyncRelayCommand RefreshGraphicsCommand { get; }
    public AsyncRelayCommand ApplyGraphicsModeCommand { get; }
    public RelayCommand OpenWindowsAppsCommand { get; }
    public RelayCommand OpenWindowsStartupCommand { get; }
    public AsyncRelayCommand RunLeftoverScanCommand { get; }
    public RelayCommand AddFanPointCommand { get; }
    public RelayCommand ValidateFanPreviewCommand { get; }
    public AsyncRelayCommand ReadFanTableCommand { get; }
    public AsyncRelayCommand ApplyFanCurveCommand { get; }
    public AsyncRelayCommand RestoreDefaultFanCommand { get; }
    public RelayCommand OpenExternalDriverLinkCommand { get; }
    public AsyncRelayCommand BrowseDriverPackageCommand { get; }
    public AsyncRelayCommand LaunchVerifiedPackageCommand { get; }
    public AsyncRelayCommand RunElevatedProbeCommand { get; }
    public AsyncRelayCommand ExportCapabilityReportCommand { get; }

    public int SelectedNavIndex
    {
        get => _selectedNavIndex;
        set => SetField(ref _selectedNavIndex, value);
    }

    public string MachineBannerText { get => _machineBannerText; set => SetField(ref _machineBannerText, value); }
    public string PrivilegeBadgeText { get => _privilegeBadgeText; set => SetField(ref _privilegeBadgeText, value); }
    public string StatusMessage { get => _statusMessage; set => SetField(ref _statusMessage, value); }
    public bool HasInterruptedOperation { get => _hasInterruptedOperation; set => SetField(ref _hasInterruptedOperation, value); }
    public string InterruptedOperationSummary { get => _interruptedOperationSummary; set => SetField(ref _interruptedOperationSummary, value); }

    public string SelectedProfileId { get => _selectedProfileId; set => SetField(ref _selectedProfileId, value); }
    public string LiveThermalModeText { get => _liveThermalModeText; set => SetField(ref _liveThermalModeText, value); }
    public string LiveWindowsPowerModeText { get => _liveWindowsPowerModeText; set => SetField(ref _liveWindowsPowerModeText, value); }
    public string LiveAcStateText { get => _liveAcStateText; set => SetField(ref _liveAcStateText, value); }
    public string ProfileBlockingNote { get => _profileBlockingNote; set => SetField(ref _profileBlockingNote, value); }

    public bool CanApplyFullProfile
    {
        get => _canApplyFullProfile;
        set
        {
            if (SetField(ref _canApplyFullProfile, value))
                Application.Current?.Dispatcher.Invoke(() => ApplyFullProfileCommand.RaiseCanExecuteChanged());
        }
    }

    public bool CanApplyPowerOnly
    {
        get => _canApplyPowerOnly;
        set
        {
            if (SetField(ref _canApplyPowerOnly, value))
                Application.Current?.Dispatcher.Invoke(() => ApplyPowerFieldsOnlyCommand.RaiseCanExecuteChanged());
        }
    }

    public string BatteryChargeText { get => _batteryChargeText; set => SetField(ref _batteryChargeText, value); }
    public string BatteryActivityText { get => _batteryActivityText; set => SetField(ref _batteryActivityText, value); }
    public string BatteryHealthText { get => _batteryHealthText; set => SetField(ref _batteryHealthText, value); }
    public string BatteryDesignCapText { get => _batteryDesignCapText; set => SetField(ref _batteryDesignCapText, value); }
    public string BatteryFullCapText { get => _batteryFullCapText; set => SetField(ref _batteryFullCapText, value); }
    public string BatteryCycleText { get => _batteryCycleText; set => SetField(ref _batteryCycleText, value); }
    public string BatteryConservationText { get => _batteryConservationText; set => SetField(ref _batteryConservationText, value); }
    public string BatteryRawDetailText { get => _batteryRawDetailText; set => SetField(ref _batteryRawDetailText, value); }
    public bool IsConservationEnabled { get => _isConservationEnabled; set => SetField(ref _isConservationEnabled, value); }

    public bool PauseGpuOnBattery
    {
        get => _pauseGpuOnBattery;
        set
        {
            if (SetField(ref _pauseGpuOnBattery, value))
            {
                _ = _stateStore.SaveSettingsAsync(_settings with { PauseGpuPollingOnBattery = value });
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
    public string GpuVramText { get => _gpuVramText; set => SetField(ref _gpuVramText, value); }
    public string GpuPstateText { get => _gpuPstateText; set => SetField(ref _gpuPstateText, value); }
    public string CpuFanRpmText { get => _cpuFanRpmText; set => SetField(ref _cpuFanRpmText, value); }
    public string GpuFanRpmText { get => _gpuFanRpmText; set => SetField(ref _gpuFanRpmText, value); }
    public string TelemetryTimestampText { get => _telemetryTimestampText; set => SetField(ref _telemetryTimestampText, value); }

    public string EffectiveGraphicsText { get => _effectiveGraphicsText; set => SetField(ref _effectiveGraphicsText, value); }
    public string PendingGraphicsText { get => _pendingGraphicsText; set => SetField(ref _pendingGraphicsText, value); }
    public bool HasPendingGraphicsReboot { get => _hasPendingGraphicsReboot; set => SetField(ref _hasPendingGraphicsReboot, value); }
    public GraphicsRoutingMode SelectedTargetGraphicsMode { get => _selectedTargetGraphicsMode; set => SetField(ref _selectedTargetGraphicsMode, value); }

    public bool ConfirmGraphicsRecoveryPath
    {
        get => _confirmGraphicsRecoveryPath;
        set
        {
            if (SetField(ref _confirmGraphicsRecoveryPath, value))
            {
                ApplyGraphicsModeCommand.RaiseCanExecuteChanged();
                _ = _stateStore.SaveSettingsAsync(_settings with { ConfirmGraphicsRecoveryPath = value });
            }
        }
    }

    public string GraphicsNoteText { get => _graphicsNoteText; set => SetField(ref _graphicsNoteText, value); }

    public string AppSearchText
    {
        get => _appSearchText;
        set
        {
            if (SetField(ref _appSearchText, value))
            {
                ApplyAppFilter();
            }
        }
    }

    public InstalledAppEntry? SelectedInstalledApp
    {
        get => _selectedInstalledApp;
        set => SetField(ref _selectedInstalledApp, value);
    }

    public string SoftwareSummaryText { get => _softwareSummaryText; set => SetField(ref _softwareSummaryText, value); }
    public string FanStatusBannerText { get => _fanStatusBannerText; set => SetField(ref _fanStatusBannerText, value); }
    public string FanValidationText { get => _fanValidationText; set => SetField(ref _fanValidationText, value); }
    public PointCollection FanPreviewPoints { get => _fanPreviewPoints; set => SetField(ref _fanPreviewPoints, value); }

    public DriverCategory SelectedDriverCategory
    {
        get => _selectedDriverCategory;
        set
        {
            if (SetField(ref _selectedDriverCategory, value))
            {
                ApplyDriverFilter();
            }
        }
    }

    public string DriverPackagePathText { get => _driverPackagePathText; set => SetField(ref _driverPackagePathText, value); }
    public string DriverPackageVerdictText { get => _driverPackageVerdictText; set => SetField(ref _driverPackageVerdictText, value); }
    public string DriverPackageSha256Text { get => _driverPackageSha256Text; set => SetField(ref _driverPackageSha256Text, value); }

    public bool CanLaunchVerifiedPackage
    {
        get => _canLaunchVerifiedPackage;
        set
        {
            if (SetField(ref _canLaunchVerifiedPackage, value))
                LaunchVerifiedPackageCommand.RaiseCanExecuteChanged();
        }
    }

    public bool UserConfirmedPackageLaunch
    {
        get => _userConfirmedPackageLaunch;
        set
        {
            if (SetField(ref _userConfirmedPackageLaunch, value))
                LaunchVerifiedPackageCommand.RaiseCanExecuteChanged();
        }
    }

    public async Task InitializeAsync()
    {
        var loaded = await _stateStore.LoadSettingsAsync().ConfigureAwait(false);
        _settings = loaded.Settings;

        var interrupted = await _writeCoordinator.GetInterruptedOperationAsync().ConfigureAwait(false);
        var identity = await _hardware.GetMachineIdentityAsync().ConfigureAwait(false);

        await RunOnUiAsync(() =>
        {
            MachineBannerText = $"{identity.Manufacturer} {identity.Model} ({identity.BiosVersion})";
            PrivilegeBadgeText = LenovoWmiProvider.IsProcessElevated()
                ? Loc.Get("StatusElevated")
                : Loc.Get("StatusStandardUser");
            _pauseGpuOnBattery = _settings.PauseGpuPollingOnBattery;
            _confirmGraphicsRecoveryPath = _settings.ConfirmGraphicsRecoveryPath;
            RaisePropertyChanged(nameof(PauseGpuOnBattery));
            RaisePropertyChanged(nameof(ConfirmGraphicsRecoveryPath));

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
        }).ConfigureAwait(false);

        await RefreshCapabilitiesAsync(allowElevation: false).ConfigureAwait(false);
        await RefreshThermalAsync(allowElevation: false).ConfigureAwait(false);
        await RefreshBatteryAsync().ConfigureAwait(false);
        await RefreshTelemetryAsync(allowWakeDgpu: false).ConfigureAwait(false);
        await RefreshGraphicsAsync(allowElevation: false).ConfigureAwait(false);
        await RefreshSoftwareAsync(includeLeftovers: false).ConfigureAwait(false);
        await RefreshDriversAsync().ConfigureAwait(false);

        _telemetryTimer.Start();
    }

    public async Task OnSystemResumeAsync()
    {
        // Never reapply profiles on resume; refresh read-only hardware state only.
        await RefreshThermalAsync(allowElevation: false).ConfigureAwait(false);
        await RefreshBatteryAsync().ConfigureAwait(false);
        await RefreshTelemetryAsync(allowWakeDgpu: false).ConfigureAwait(false);
        await RefreshGraphicsAsync(allowElevation: false).ConfigureAwait(false);
    }

    public void SetWindowVisibility(bool visible)
    {
        _isWindowVisible = visible;
    }

    private void SelectProfile(string profileId)
    {
        SelectedProfileId = profileId;
        var profile = _settings.Profiles.FirstOrDefault(p => p.Id == profileId)
                      ?? ProfileSeedFactory.CreatePristineReference(ProfileSeedFactory.DailyId);
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
        EvaluateProfileApplyButtons(profile);
    }

    private void LoadFanPointsFromProfile(PerformanceProfile profile)
    {
        FanPoints.Clear();
        var channel = profile.FanCurve.Channels.FirstOrDefault();
        if (channel is not null)
        {
            foreach (var pt in channel.Points)
            {
                FanPoints.Add(new FanPointRow
                {
                    TemperatureC = pt.TemperatureC,
                    FanSpeedValue = pt.FanSpeedValue,
                    SpeedUnit = pt.SpeedUnit
                });
            }
        }

        FanStatusBannerText = profile.FanCurve.Status == FanCurveStatus.PendingCapture
            ? Loc.Get("MsgPendingFanCapture")
            : $"{profile.FanCurve.Status}: {profile.FanCurve.SourceDescription}";
        UpdateFanCurvePreview();
    }

    private void EvaluateProfileApplyButtons(PerformanceProfile profile)
    {
        var isAc = _latestThermalSnapshot?.IsOnAcPower ?? true;
        var preflight = _validator.PreflightProfile(profile, _settings.FieldContracts, isAc);
        CanApplyFullProfile = preflight.CanApplyFullProfile;
        CanApplyPowerOnly = preflight.CanApplyPowerFieldsOnly;
        ProfileBlockingNote = preflight.BlockingReasons.Count > 0
            ? string.Join(" | ", preflight.BlockingReasons.Take(3))
            : "Ready";
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
        var updatedPower = ParseDraftSettingsFromRows();
        var updatedProfiles = _settings.Profiles.Select(p =>
            p.Id == SelectedProfileId
                ? p with { Power = updatedPower, LastModifiedUtc = DateTimeOffset.UtcNow }
                : p).ToList();

        _settings = _settings with
        {
            ActiveSelectedProfileId = SelectedProfileId,
            Profiles = updatedProfiles
        };
        await _stateStore.SaveSettingsAsync(_settings).ConfigureAwait(false);
        await RunOnUiAsync(() =>
        {
            var active = _settings.Profiles.First(p => p.Id == SelectedProfileId);
            EvaluateProfileApplyButtons(active);
            StatusMessage = $"Saved draft for '{SelectedProfileId}' to settings.json (no hardware write performed).";
        }).ConfigureAwait(false);
    }

    private async Task ResetProfileToReferenceAsync()
    {
        var reference = ProfileSeedFactory.CreatePristineReference(SelectedProfileId);
        var updatedProfiles = _settings.Profiles.Select(p => p.Id == SelectedProfileId ? reference : p).ToList();
        _settings = _settings with { Profiles = updatedProfiles };
        await _stateStore.SaveSettingsAsync(_settings).ConfigureAwait(false);
        await RunOnUiAsync(() =>
        {
            SelectProfile(SelectedProfileId);
            StatusMessage = $"Restored '{SelectedProfileId}' draft to pristine Section 3 reference values.";
        }).ConfigureAwait(false);
    }

    private async Task ApplyFullProfileAsync()
    {
        await SaveProfileDraftAsync().ConfigureAwait(false);
        var profile = _settings.Profiles.First(p => p.Id == SelectedProfileId);
        var report = await _writeCoordinator.ApplyPowerFieldsAsync(profile, _settings.FieldContracts).ConfigureAwait(false);
        await RunOnUiAsync(() =>
        {
            StatusMessage = report.Summary;
            if (report.ReadBackSnapshot is not null)
            {
                ApplyThermalSnapshotToUi(report.ReadBackSnapshot);
            }
        }).ConfigureAwait(false);
    }

    private async Task ApplyPowerFieldsOnlyAsync()
    {
        await SaveProfileDraftAsync().ConfigureAwait(false);
        var profile = _settings.Profiles.First(p => p.Id == SelectedProfileId);
        var report = await _writeCoordinator.ApplyPowerFieldsAsync(profile, _settings.FieldContracts).ConfigureAwait(false);
        await RunOnUiAsync(() =>
        {
            StatusMessage = report.Summary;
            if (report.ReadBackSnapshot is not null)
            {
                ApplyThermalSnapshotToUi(report.ReadBackSnapshot);
            }
        }).ConfigureAwait(false);
    }

    private async Task SetStockThermalModeAsync(object? param)
    {
        if (param is not string modeName || !Enum.TryParse<ThermalMode>(modeName, out var mode))
        {
            return;
        }

        var report = await _writeCoordinator.SetStockThermalModeAsync(mode).ConfigureAwait(false);
        await RunOnUiAsync(() =>
        {
            StatusMessage = report.Summary;
            if (report.ReadBackSnapshot is not null)
            {
                ApplyThermalSnapshotToUi(report.ReadBackSnapshot);
            }
        }).ConfigureAwait(false);
    }

    private async Task RefreshThermalAsync(bool allowElevation)
    {
        var snap = await _hardware.ReadLiveThermalSnapshotAsync(allowElevation).ConfigureAwait(false);
        await RunOnUiAsync(() => ApplyThermalSnapshotToUi(snap)).ConfigureAwait(false);
    }

    private void ApplyThermalSnapshotToUi(LiveThermalSnapshot snap)
    {
        _latestThermalSnapshot = snap;
        var matchedProfile = _settings.Profiles.FirstOrDefault(p =>
            ProfileValidationService.DoesLiveMatchProfile(snap, p, _settings.FieldContracts));
        var modeLabel = matchedProfile is not null
            ? $"{snap.ThermalMode} ({matchedProfile.Name})"
            : snap.IsVerifiedLiveRead ? snap.ThermalMode.ToString() : $"{Loc.Get("LblUnavailable")} ({snap.StatusNote})";

        LiveThermalModeText = modeLabel;
        LiveWindowsPowerModeText = $"{snap.WindowsPowerMode} ({snap.ActivePowerSchemeName})";
        LiveAcStateText = snap.IsOnAcPower ? Loc.Get("MsgAcOnline") : Loc.Get("MsgOnBattery");

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

        var current = _settings.Profiles.FirstOrDefault(p => p.Id == SelectedProfileId);
        if (current is not null)
        {
            EvaluateProfileApplyButtons(current);
        }
    }

    private async Task RefreshBatteryAsync()
    {
        var snap = await _battery.ReadStatusAsync().ConfigureAwait(false);
        await RunOnUiAsync(() =>
        {
            _isConservationSupported = snap.IsConservationWriteSupported;
            IsConservationEnabled = snap.LenovoChargeMode == BatteryChargeMode.Conservation;
            BatteryChargeText = snap.ChargePercent is not null ? $"{snap.ChargePercent}%" : Loc.Get("LblUnavailable");
            BatteryActivityText = $"{snap.Activity} ({(snap.IsAcOnline ? Loc.Get("MsgAcOnline") : Loc.Get("MsgOnBattery"))})";
            BatteryHealthText = snap.HealthPercent is not null ? $"{snap.HealthPercent:F2}%" : Loc.Get("LblUnavailable");
            BatteryDesignCapText = snap.DesignCapacityMwh is not null ? $"{snap.DesignCapacityMwh:N0} mWh" : Loc.Get("LblUnavailable");
            BatteryFullCapText = snap.FullChargeCapacityMwh is not null ? $"{snap.FullChargeCapacityMwh:N0} mWh" : Loc.Get("LblUnavailable");
            BatteryCycleText = snap.CycleCount is not null ? snap.CycleCount.Value.ToString() : Loc.Get("LblUnavailable");
            BatteryConservationText = snap.LenovoChargeMode switch
            {
                BatteryChargeMode.Conservation => Loc.Get("MsgConservationOn"),
                BatteryChargeMode.RapidCharge => Loc.Get("MsgRapidCharge"),
                BatteryChargeMode.Normal => Loc.Get("MsgConservationOff"),
                _ => Loc.Get("LblUnavailable")
            };
            BatteryRawDetailText = snap.ProviderNote;
            ToggleConservationCommand.RaiseCanExecuteChanged();
        }).ConfigureAwait(false);
    }

    private async Task ToggleConservationAsync()
    {
        var target = !IsConservationEnabled;
        var res = await _battery.SetConservationModeAsync(target).ConfigureAwait(false);
        await RefreshBatteryAsync().ConfigureAwait(false);
        await RunOnUiAsync(() => StatusMessage = res.Message).ConfigureAwait(false);
    }

    private async Task RefreshTelemetryAsync(bool allowWakeDgpu)
    {
        var snap = await _telemetry.ReadSnapshotAsync(allowWakeDgpu || !PauseGpuOnBattery).ConfigureAwait(false);
        await RunOnUiAsync(() =>
        {
            CpuUsageText = FormatReading(snap.CpuUtilization, "F1");
            CpuTempText = FormatReading(snap.CpuTemperature, "F0");
            MemoryUsageText = snap.MemoryUsedGb.Status == SensorStatus.Valid && snap.MemoryTotalGb.Status == SensorStatus.Valid
                ? $"{snap.MemoryUsedGb.Value:F1} / {snap.MemoryTotalGb.Value:F1} GB ({snap.MemoryUsedPercent.Value:F0}%)"
                : Loc.Get("LblUnavailable");
            DiskActivityText = snap.DiskActivePercent.Status == SensorStatus.Valid
                ? $"{snap.DiskActivePercent.Value:F1}% | {snap.DiskThroughputMbPerSec.Value:F2} MB/s"
                : $"{snap.DiskActivePercent.Status} ({snap.DiskActivePercent.Note})";

            DiskCapacities.Clear();
            foreach (var d in snap.DiskCapacities)
            {
                DiskCapacities.Add(d);
            }

            GpuNameText = snap.GpuName ?? "NVIDIA RTX (dGPU)";
            GpuUtilText = FormatReading(snap.GpuUtilization, "F0");
            GpuTempText = FormatReading(snap.GpuTemperature, "F0");
            GpuPowerText = FormatReading(snap.GpuPowerWatts, "F1");
            GpuVramText = snap.GpuMemoryUsedMb.Status == SensorStatus.Valid
                ? $"{snap.GpuMemoryUsedMb.Value:F0} / {snap.GpuMemoryTotalMb.Value:F0} MB"
                : FormatReading(snap.GpuMemoryUsedMb, "F0");
            GpuPstateText = snap.GpuPowerState ?? "--";
            CpuFanRpmText = FormatReading(snap.CpuFanRpm, "D");
            GpuFanRpmText = FormatReading(snap.GpuFanRpm, "D");
            TelemetryTimestampText = snap.CapturedAtUtc.LocalDateTime.ToString("HH:mm:ss");
        }).ConfigureAwait(false);
    }

    private static string FormatReading<T>(SensorReading<T> reading, string format) where T : struct =>
        reading.Status switch
        {
            SensorStatus.Valid when reading.Value is double d => $"{d.ToString(format)} {reading.Unit}",
            SensorStatus.Valid when reading.Value is int i => $"{i} {reading.Unit}",
            SensorStatus.PausedOnBattery => Loc.Get("MsgPausedOnBattery"),
            SensorStatus.Stale => $"Stale ({reading.Note})",
            SensorStatus.Sleeping => "Sleeping",
            _ => $"{Loc.Get("LblUnavailable")} ({reading.Note ?? reading.Source})"
        };

    private async Task RefreshGraphicsAsync(bool allowElevation)
    {
        var snap = await _hardware.ReadGraphicsStatusAsync(allowElevation).ConfigureAwait(false);
        await RunOnUiAsync(() =>
        {
            EffectiveGraphicsText = snap.IsVerifiedLiveRead ? snap.EffectiveMode.ToString() : Loc.Get("LblUnavailable");
            PendingGraphicsText = snap.PendingMode?.ToString() ?? "--";
            HasPendingGraphicsReboot = snap.RebootRequired;
            GraphicsNoteText = snap.StatusNote;

            SupportedGraphicsModes.Clear();
            foreach (var mode in snap.SupportedModes)
            {
                SupportedGraphicsModes.Add(mode);
            }
        }).ConfigureAwait(false);
    }

    private async Task ApplyGraphicsModeAsync()
    {
        var res = await _hardware.SetGraphicsModeAsync(SelectedTargetGraphicsMode).ConfigureAwait(false);
        await RunOnUiAsync(() => StatusMessage = res.Message).ConfigureAwait(false);
        await RefreshGraphicsAsync(allowElevation: false).ConfigureAwait(false);
    }

    private async Task RefreshSoftwareAsync(bool includeLeftovers)
    {
        var snap = await _software.ScanAsync(includeLeftovers).ConfigureAwait(false);
        await RunOnUiAsync(() =>
        {
            _allApps = snap.InstalledApps;
            ApplyAppFilter();

            StartupEntries.Clear();
            foreach (var entry in snap.StartupEntries)
            {
                StartupEntries.Add(entry);
            }

            if (includeLeftovers)
            {
                LeftoverFindings.Clear();
                foreach (var finding in snap.LenovoFindings)
                {
                    LeftoverFindings.Add(finding);
                }
            }

            SoftwareSummaryText = $"Apps: {snap.InstalledApps.Count} | Startup: {snap.StartupEntries.Count} | Lenovo Findings: {LeftoverFindings.Count}";
        }).ConfigureAwait(false);
    }

    private void ApplyAppFilter()
    {
        FilteredApps.Clear();
        var q = AppSearchText?.Trim() ?? string.Empty;
        foreach (var app in _allApps)
        {
            if (string.IsNullOrEmpty(q) ||
                app.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                app.Publisher.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                FilteredApps.Add(app);
            }
        }
    }

    private void AddFanPoint()
    {
        var nextTemp = FanPoints.Count == 0 ? 40 : Math.Min(100, FanPoints[^1].TemperatureC + 10);
        var nextSpeed = FanPoints.Count == 0 ? 1800 : Math.Min(5400, FanPoints[^1].FanSpeedValue + 400);
        FanPoints.Add(new FanPointRow { TemperatureC = nextTemp, FanSpeedValue = nextSpeed, SpeedUnit = "RPM" });
        UpdateFanCurvePreview();
    }

    private void UpdateFanCurvePreview()
    {
        var pts = new PointCollection();
        const double width = 420.0;
        const double height = 180.0;
        const double minT = 30.0, maxT = 100.0;
        const double minRpm = 0.0, maxRpm = 5600.0;

        foreach (var row in FanPoints.OrderBy(p => p.TemperatureC))
        {
            var x = Math.Clamp((row.TemperatureC - minT) / (maxT - minT), 0.0, 1.0) * width;
            var y = height - Math.Clamp((row.FanSpeedValue - minRpm) / (maxRpm - minRpm), 0.0, 1.0) * height;
            pts.Add(new Point(x, y));
        }

        FanPreviewPoints = pts;
        var profile = _settings.Profiles.FirstOrDefault(p => p.Id == SelectedProfileId);
        if (profile is not null && FanPoints.Count > 0)
        {
            var channel = new FanChannelCurve(
                0, "Preview Channel", "CPU",
                FanPoints.Select(p => new FanCurvePoint(p.TemperatureC, p.FanSpeedValue, p.SpeedUnit)).ToList(),
                30, 100, 1, 0, 5600, 100, "RPM");
            var val = _validator.ValidateFanCurve(new FanCurveProfile(FanCurveStatus.Captured, "Draft editor", [channel]));
            FanValidationText = val.IsValid ? "Draft curve geometry valid (pending firmware capture/write validation)." : string.Join(" | ", val.Errors);
        }
        else
        {
            FanValidationText = Loc.Get("MsgPendingFanCapture");
        }
    }

    private async Task ReadFanFromFirmwareAsync()
    {
        var res = await _hardware.ReadFanCurveAsync(allowElevation: true).ConfigureAwait(false);
        await RunOnUiAsync(() =>
        {
            StatusMessage = res.Message;
            if (res.Success && res.Value is not null)
            {
                FanPoints.Clear();
                var ch = res.Value.Channels.FirstOrDefault();
                if (ch is not null)
                {
                    foreach (var pt in ch.Points)
                    {
                        FanPoints.Add(new FanPointRow { TemperatureC = pt.TemperatureC, FanSpeedValue = pt.FanSpeedValue, SpeedUnit = pt.SpeedUnit });
                    }
                }

                FanStatusBannerText = $"{res.Value.Status}: {res.Value.SourceDescription}";
                UpdateFanCurvePreview();
            }
        }).ConfigureAwait(false);
    }

    private async Task ApplyFanCurveAsync()
    {
        var channel = new FanChannelCurve(
            0, "CPU/GPU", "CPU",
            FanPoints.Select(p => new FanCurvePoint(p.TemperatureC, p.FanSpeedValue, p.SpeedUnit)).ToList(),
            30, 100, 1, 0, 5600, 100);
        var curve = new FanCurveProfile(FanCurveStatus.Captured, "User draft", [channel]);
        var res = await _hardware.ApplyFanCurveAsync(curve).ConfigureAwait(false);
        await RunOnUiAsync(() => StatusMessage = res.Message).ConfigureAwait(false);
    }

    private async Task RestoreDefaultFanAsync()
    {
        var res = await _hardware.RestoreDefaultFanCurveAsync().ConfigureAwait(false);
        await RunOnUiAsync(() => StatusMessage = res.Message).ConfigureAwait(false);
    }

    private async Task RefreshDriversAsync()
    {
        var list = await _drivers.ListDriversAsync().ConfigureAwait(false);
        await RunOnUiAsync(() =>
        {
            _allDrivers = list;
            ApplyDriverFilter();
        }).ConfigureAwait(false);
    }

    private void ApplyDriverFilter()
    {
        FilteredDrivers.Clear();
        foreach (var d in _allDrivers)
        {
            if (SelectedDriverCategory == DriverCategory.All || d.Category == SelectedDriverCategory)
            {
                FilteredDrivers.Add(d);
            }
        }
    }

    private async Task BrowseAndInspectDriverPackageAsync()
    {
        var dlg = new OpenFileDialog
        {
            Title = Loc.Get("MsgBrowseFile"),
            Filter = "Driver Packages (*.exe;*.inf;*.cat)|*.exe;*.inf;*.cat|All Files (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true)
        {
            return;
        }

        var machine = _latestCapabilityReport?.Machine ?? await _hardware.GetMachineIdentityAsync().ConfigureAwait(false);
        var pkg = await _drivers.InspectLocalPackageAsync(dlg.FileName).ConfigureAwait(false);
        var decision = DriverApplicabilityEvaluator.Evaluate(pkg, machine);

        await RunOnUiAsync(() =>
        {
            _inspectedPackage = pkg;
            _inspectedDecision = decision;
            DriverPackagePathText = pkg.FilePath;
            DriverPackageSha256Text = $"SHA-256: {pkg.Sha256Hex} | Signer: {pkg.SignerSubject ?? "(unsigned)"} | Ver: {pkg.FileVersion ?? "--"}";
            DriverPackageVerdictText = $"[{decision.VerdictCode}] {decision.Reason}";
            UserConfirmedPackageLaunch = false;
            CanLaunchVerifiedPackage = decision.IsAllowedToLaunch;
        }).ConfigureAwait(false);
    }

    private async Task LaunchVerifiedDriverPackageAsync()
    {
        if (_inspectedPackage is null || _inspectedDecision is null || !_inspectedDecision.IsAllowedToLaunch || !UserConfirmedPackageLaunch)
        {
            return;
        }

        var res = await _drivers.LaunchVerifiedInstallerAsync(_inspectedPackage.FilePath, _inspectedDecision.VerifiedSha256Hex).ConfigureAwait(false);
        await RunOnUiAsync(() => StatusMessage = res.Message).ConfigureAwait(false);
        await RefreshDriversAsync().ConfigureAwait(false);
    }

    private async Task RefreshCapabilitiesAsync(bool allowElevation)
    {
        var report = await _hardware.ProbeCapabilitiesAsync(allowElevation).ConfigureAwait(false);
        await RunOnUiAsync(() =>
        {
            _latestCapabilityReport = report;
            Capabilities.Clear();
            foreach (var c in report.Capabilities)
            {
                Capabilities.Add(c);
            }

            UnresolvedItems.Clear();
            foreach (var u in report.UnresolvedItems)
            {
                UnresolvedItems.Add(u);
            }

            if (allowElevation)
            {
                StatusMessage = report.IsElevated
                    ? "Elevated capability probe succeeded."
                    : "Capability probe finished in standard-user mode (elevation cancelled or unavailable).";
            }
        }).ConfigureAwait(false);
    }

    private async Task ExportCapabilityReportAsync()
    {
        var dlg = new SaveFileDialog
        {
            FileName = $"utility-capability-report-{DateTime.Now:yyyyMMdd-HHmmss}.json",
            Filter = "JSON Files (*.json)|*.json"
        };
        if (dlg.ShowDialog() != true)
        {
            return;
        }

        var payload = new
        {
            CapabilityReport = _latestCapabilityReport,
            LiveThermal = _latestThermalSnapshot,
            ExportedAtUtc = DateTimeOffset.UtcNow
        };
        await File.WriteAllTextAsync(dlg.FileName, JsonSerializer.Serialize(payload, ReportJsonOptions)).ConfigureAwait(false);
        await RunOnUiAsync(() => StatusMessage = $"Exported capability report to {dlg.FileName}").ConfigureAwait(false);
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

    public void Dispose()
    {
        _telemetryTimer.Stop();
        _telemetryTimer.Dispose();
    }
}
