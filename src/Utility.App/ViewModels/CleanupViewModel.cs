using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Utility.App.Localization;
using Utility.App.Views;
using Utility.Platform.Providers;

namespace Utility.App.ViewModels;

internal sealed class CleanupViewModel : ViewModelBase
{
    private readonly WindowsUninstallService _uninstall = new();
    private readonly UninstallResidualService _residual;
    private CancellationTokenSource? _scanCancellation;
    private IReadOnlyList<CleanupAppRow> _inventory = [];
    private UninstallTarget? _lastUninstall;
    private string _search = string.Empty;
    private bool _showComponents;
    private string _componentSummary = string.Empty;
    private string _status;
    private bool _isBusy;
    private CleanupAppRow? _selectedApp;
    private IReadOnlyList<CleanupAppRow> _appRows = [];
    // Ticked apps by stable id, so ticks survive search filtering and list refreshes.
    private readonly HashSet<string> _checkedAppIds = [];
    private readonly List<AsyncRelayCommand> _actions = [];

    public CleanupViewModel()
    {
        _residual = new UninstallResidualService(_uninstall);
        _status = Loc.Get("CleanupSoftwareHint");

        RefreshAppsCommand = Action(() => RunAsync(RefreshAppsAsync, cancellable: true));
        UninstallCommand = Action(() => RunAsync(UninstallAsync),
            () => UninstallTargets().Count > 0);
        ScanResidualCommand = Action(() => RunAsync(_ =>
        {
            if (_lastUninstall is { } target)
                ReviewResiduals(target);
            return Task.CompletedTask;
        }), () => _lastUninstall is not null);
        _lastUninstall = _residual.LoadLastUninstall();
    }

    public AsyncRelayCommand RefreshAppsCommand { get; }
    public AsyncRelayCommand UninstallCommand { get; }
    public AsyncRelayCommand ScanResidualCommand { get; }

    /// <summary>
    /// Current status line. Results are also raised through <see cref="StatusReported"/> (the auto-hiding toast);
    /// progress lines ("正在…") are set with <see cref="SetProgress"/> and stay silent, so a toast always means
    /// something finished.
    /// </summary>
    public string Status
    {
        get => _status;
        private set
        {
            SetField(ref _status, value);
            if (!string.IsNullOrWhiteSpace(value))
                StatusReported?.Invoke(value);
        }
    }

    private void SetProgress(string text) => SetField(ref _status, text, nameof(Status));

    public event Action<string>? StatusReported;

    public string Search
    {
        get => _search;
        set
        {
            if (SetField(ref _search, value))
                FilterApps();
        }
    }

    /// <summary>Also list runtimes, codecs, extensions and other components (hidden by default to avoid mistakes).</summary>
    public bool ShowComponents
    {
        get => _showComponents;
        set
        {
            if (SetField(ref _showComponents, value))
                FilterApps();
        }
    }

    /// <summary>"另有 N 个系统组件已隐藏" next to the toggle; empty when there are none or they are shown.</summary>
    public string ComponentSummary { get => _componentSummary; private set => SetField(ref _componentSummary, value); }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetField(ref _isBusy, value))
                return;
            foreach (var command in _actions)
                command.RaiseCanExecuteChanged();
        }
    }

    public CleanupAppRow? SelectedApp
    {
        get => _selectedApp;
        set
        {
            if (SetField(ref _selectedApp, value))
                UninstallCommand.RaiseCanExecuteChanged();
        }
    }

    public IReadOnlyList<CleanupAppRow> AppRows { get => _appRows; private set => SetField(ref _appRows, value); }

    public void CancelScan() => _scanCancellation?.Cancel();

    private AsyncRelayCommand Action(Func<Task> run, Func<bool>? enabled = null)
    {
        var command = new AsyncRelayCommand(run, () => !IsBusy && (enabled?.Invoke() ?? true));
        _actions.Add(command);
        return command;
    }

    private async Task RunAsync(Func<CancellationToken, Task> run, bool cancellable = false)
    {
        IsBusy = true;
        using var token = new CancellationTokenSource();
        _scanCancellation = cancellable ? token : null;
        try
        {
            await run(token.Token);
        }
        catch (OperationCanceledException)
        {
            Status = Loc.Get("CleanupCancelled");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Status = Loc.Get("CleanupCancelled");
        }
        catch (Exception ex)
        {
            Status = Loc.Format("MsgActionFailed", ex.Message);
        }
        finally
        {
            _scanCancellation = null;
            IsBusy = false;
        }
    }

    private async Task RefreshAppsAsync(CancellationToken ct)
    {
        SetProgress(Loc.Get("CleanupLoadingApps"));
        var result = await _uninstall.ListAsync(ct, _inventory.Count == 0 ? rows =>
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                ct.ThrowIfCancellationRequested();
                SetInventory(rows);
            }).Task : null);
        SetInventory(result.Apps);
        Status = result.StoreError is null
            ? Loc.Format("CleanupAppsFound", _inventory.Count)
            : Loc.Format("CleanupStoreError", result.StoreError);
    }

    private void SetInventory(IReadOnlyList<UninstallTarget> targets)
    {
        _inventory = targets.Select(target =>
            new CleanupAppRow(target, _checkedAppIds.Contains(AppKey(target)), OnAppCheckChanged)).ToArray();
        // A component named like a real app (PixPin's context menu, WeChat's share extension…) gets its package
        // name appended, so the two rows can be told apart.
        var appNames = _inventory.Where(row => !row.IsComponent).Select(row => row.BaseName)
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        foreach (var row in _inventory.Where(row => row.IsComponent && row.Target.PackageName is { } package &&
                                                    !package.Equals(row.BaseName, StringComparison.OrdinalIgnoreCase) &&
                                                    (appNames.Contains(row.BaseName) || appNames.Any(name => name.Contains(row.BaseName, StringComparison.CurrentCultureIgnoreCase)))))
            row.Disambiguate();
        // Sort by the name people see (the service sorts by the internal package name).
        _inventory = _inventory.OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        FilterApps();
    }

    private void FilterApps()
    {
        var query = Search.Trim();
        var before = SelectedApp?.Target.App.StableId;
        // Searching also finds hidden components, so a known name is never "missing".
        var matches = _inventory.Where(app => query.Length == 0 ||
                app.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                app.Target.App.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                app.Publisher.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        AppRows = ShowComponents || query.Length > 0 ? matches : matches.Where(app => !app.IsComponent).ToArray();
        var hidden = matches.Length - AppRows.Count;
        ComponentSummary = hidden > 0 ? Loc.Format("CleanupComponentsHidden", hidden) : string.Empty;
        SelectedApp = AppRows.FirstOrDefault(row => row.Target.App.StableId == before);
        UninstallCommand.RaiseCanExecuteChanged();
    }

    private static string AppKey(UninstallTarget target) => target.App.StableId;

    private void OnAppCheckChanged(CleanupAppRow row)
    {
        var key = AppKey(row.Target);
        if (row.Selected)
            _checkedAppIds.Add(key);
        else
            _checkedAppIds.Remove(key);
        UninstallCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Ticked removable apps; falls back to the highlighted row when nothing is ticked.</summary>
    private IReadOnlyList<UninstallTarget> UninstallTargets()
    {
        var ticked = _inventory.Where(row => row.Selected).Select(row => row.Target).ToArray();
        if (ticked.Length > 0)
            return ticked;
        return SelectedApp?.Target is { CanRemove: true } target ? [target] : [];
    }

    private async Task UninstallAsync(CancellationToken ct)
    {
        // Apps are uninstalled one at a time, each with its own confirmation and vendor uninstaller.
        var targets = UninstallTargets();
        AppRemovalResult? lastResult = null;
        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();
            var message = target.PackageName == "Microsoft.WindowsStore"
                ? Loc.Get("CleanupConfirmStoreUninstall")
                : Loc.Format("CleanupConfirmUninstall", target.FriendlyName ?? target.App.DisplayName);
            if (!Confirm(message))
                continue;

            RememberUninstall(target);
            SetProgress(Loc.Format("CleanupWaitingUninstall", target.FriendlyName ?? target.App.DisplayName));
            lastResult = await _uninstall.UninstallAsync(target);
            _checkedAppIds.Remove(AppKey(target));
            if (lastResult.Removed)
                ReviewResiduals(target, lastResult.RestartRequired);
        }

        if (lastResult is null)
            return;
        await RefreshAppsAsync(ct);
        if (!lastResult.Removed)
            Status = Loc.Format("CleanupUninstallPending", lastResult.Detail ?? string.Empty);
    }

    private void RememberUninstall(UninstallTarget target)
    {
        _lastUninstall = target;
        _residual.SaveLastUninstall(target);
        ScanResidualCommand.RaiseCanExecuteChanged();
    }

    private void ReviewResiduals(UninstallTarget target, bool restartRequired = false) =>
        new UninstallResidualWindow(_residual, target, restartRequired)
        {
            Owner = Application.Current.MainWindow
        }.ShowDialog();

    private static bool Confirm(string message) => MessageBox.Show(Application.Current.MainWindow,
        message, Loc.Get("NavCleanup"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    internal static string FormatSize(long bytes) => bytes >= 1024L * 1024 * 1024
        ? (bytes / 1073741824d).ToString("0.##", CultureInfo.CurrentCulture) + " GiB"
        : bytes >= 1024L * 1024
            ? (bytes / 1048576d).ToString("0.##", CultureInfo.CurrentCulture) + " MiB"
            : bytes >= 1024
                ? (bytes / 1024d).ToString("0.##", CultureInfo.CurrentCulture) + " KiB"
                : bytes.ToString(CultureInfo.CurrentCulture) + " B";
}

internal sealed class CleanupItemRow : ViewModelBase
{
    private bool _selected;
    private CleanupItemRow(ScannedFile? file, AppResidual? residual, string evidence)
    {
        File = file;
        Residual = residual;
        Evidence = evidence;
    }

    public ScannedFile? File { get; }
    public AppResidual? Residual { get; }
    public bool Selected { get => _selected; set => SetField(ref _selected, value); }
    public string Path => Residual?.Path ?? File!.Path;
    public string Size => File is null ? "—" : CleanupViewModel.FormatSize(File.Bytes);
    public string Evidence { get; }

    public static CleanupItemRow ForDisk(ScannedFile file) => new(file, null,
        file.Group is int group ? Loc.Format("CleanupDuplicateGroup", group) : file.LastWriteUtc.ToLocalTime().ToString("g"));

    /// <summary>A file row whose third column shows a status note (e.g. 已清理 / 正在使用).</summary>
    public static CleanupItemRow WithNote(ScannedFile file, string note) => new(file, null, note);

    public static CleanupItemRow ForResidual(AppResidual residual) =>
        new(residual.File, residual, Loc.Get("CleanupEvidence" + residual.Evidence));
}

/// <summary>One installed app on the software page; <see cref="Selected"/> is the row's tick box.</summary>
internal sealed class CleanupAppRow : ViewModelBase
{
    private readonly Action<CleanupAppRow> _onSelectedChanged;
    private bool _selected;
    private bool _iconRequested;
    private ImageSource? _icon;

    public CleanupAppRow(UninstallTarget target, bool selected, Action<CleanupAppRow> onSelectedChanged)
    {
        Target = target;
        _selected = selected;
        _onSelectedChanged = onSelectedChanged;
    }

    public UninstallTarget Target { get; }

    public bool Selected
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
                _onSelectedChanged(this);
        }
    }

    private string? _name;
    internal string BaseName => Target.FriendlyName ?? Target.App.DisplayName;
    public string Name => _name ?? BaseName;
    internal void Disambiguate() => _name = $"{BaseName}（{Target.PackageName}）";
    public string Publisher => Target.FriendlyPublisher ?? Target.App.Publisher;
    public string Version => Target.App.Version;
    public bool IsComponent => Target.IsComponent;
    public string Type => Loc.Get(IsComponent ? "AppTypeComponent" : Target.PackageName is null ? "AppTypeDesktop" : "AppTypeStore");
    /// <summary>Package name under the friendly one, so the technical identity stays visible (tooltip).</summary>
    public string Detail => Target.PackageName is { } package && package != Name ? package : Name;
    public ImageSource? Icon { get => _icon; private set => SetField(ref _icon, value); }

    public async Task LoadIconAsync()
    {
        if (_iconRequested)
            return;
        _iconRequested = true;
        Icon = await InstalledAppIconLoader.LoadAsync(Target);
    }
}
