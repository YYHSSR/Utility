using System.Globalization;
using System.Windows;
using Utility.App.Localization;
using Utility.Platform.Providers;

namespace Utility.App.ViewModels;

internal sealed class CleanupViewModel : ViewModelBase
{
    private readonly WindowsUninstallService _uninstall = new();
    private readonly UninstallResidualService _residual;
    private CancellationTokenSource? _scanCancellation;
    private IReadOnlyList<UninstallTarget> _inventory = [];
    private UninstallTarget? _lastUninstall;
    private string _search = string.Empty;
    private string _status;
    private bool _isBusy;
    private bool _showResidual;
    private CleanupAppRow? _selectedApp;
    private IReadOnlyList<CleanupAppRow> _appRows = [];
    // Ticked apps by stable id, so ticks survive search filtering and list refreshes.
    private readonly HashSet<string> _checkedAppIds = [];
    private IReadOnlyList<CleanupItemRow> _residualRows = [];
    private readonly List<AsyncRelayCommand> _actions = [];

    public CleanupViewModel()
    {
        _residual = new UninstallResidualService(_uninstall);
        _status = Loc.Get("CleanupSoftwareHint");

        RefreshAppsCommand = Action(() => RunAsync(RefreshAppsAsync, cancellable: true));
        UninstallCommand = Action(() => RunAsync(UninstallAsync),
            () => UninstallTargets().Count > 0);
        ScanResidualCommand = Action(() => RunAsync(ScanResidualAsync, cancellable: true), () => _lastUninstall is not null);
        RemoveResidualCommand = Action(() => RunAsync(RemoveResidualAsync), () => ResidualRows.Count > 0);
        ReturnToAppsCommand = new(() =>
        {
            ShowResidual = false;
            SetField(ref _status, Loc.Get("CleanupSoftwareHint"), nameof(Status));
        }, () => !IsBusy);
        SelectItemsCommand = new(SelectItems, () => !IsBusy);
        _lastUninstall = _residual.LoadLastUninstall();
    }

    public AsyncRelayCommand RefreshAppsCommand { get; }
    public AsyncRelayCommand UninstallCommand { get; }
    public AsyncRelayCommand ScanResidualCommand { get; }
    public AsyncRelayCommand RemoveResidualCommand { get; }
    public RelayCommand ReturnToAppsCommand { get; }
    public RelayCommand SelectItemsCommand { get; }

    public bool IsAppPage => !ShowResidual;
    public bool IsResidualPage => ShowResidual;

    /// <summary>
    /// Current status line. Every result/progress update is also raised through <see cref="StatusReported"/>
    /// (shown as the auto-hiding toast); page hints and restored statuses are set silently.
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

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetField(ref _isBusy, value))
                return;
            foreach (var command in _actions)
                command.RaiseCanExecuteChanged();
            ReturnToAppsCommand.RaiseCanExecuteChanged();
            SelectItemsCommand.RaiseCanExecuteChanged();
        }
    }

    public bool ShowResidual
    {
        get => _showResidual;
        private set
        {
            if (!SetField(ref _showResidual, value))
                return;
            RaisePropertyChanged(nameof(IsAppPage));
            RaisePropertyChanged(nameof(IsResidualPage));
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
    public IReadOnlyList<CleanupItemRow> ResidualRows { get => _residualRows; private set => SetField(ref _residualRows, value); }

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

    private void SelectItems()
    {
        foreach (var row in ResidualRows)
            row.Selected = true;
    }

    private async Task RefreshAppsAsync(CancellationToken ct)
    {
        Status = Loc.Get("CleanupLoadingApps");
        var result = await _uninstall.ListAsync(ct, _inventory.Count == 0 ? rows =>
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                ct.ThrowIfCancellationRequested();
                _inventory = rows;
                FilterApps();
            }).Task : null);
        _inventory = result.Apps;
        FilterApps();
        Status = result.StoreError is null
            ? Loc.Format("CleanupAppsFound", _inventory.Count, _inventory.Count(app => app.CanRemove))
            : Loc.Format("CleanupStoreError", result.StoreError);
    }

    private void FilterApps()
    {
        var query = Search.Trim();
        var before = SelectedApp?.Target.App.StableId;
        AppRows = _inventory.Where(app => query.Length == 0 ||
                app.App.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                app.App.Publisher.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .Select(app => new CleanupAppRow(app, _checkedAppIds.Contains(AppKey(app)), OnAppCheckChanged)).ToArray();
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
        var ticked = _inventory.Where(app => app.CanRemove && _checkedAppIds.Contains(AppKey(app))).ToArray();
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
                : Loc.Format("CleanupConfirmUninstall", target.App.DisplayName);
            if (!Confirm(message))
                continue;

            RememberUninstall(target);
            Status = Loc.Format("CleanupWaitingUninstall", target.App.DisplayName);
            lastResult = await _uninstall.UninstallAsync(target);
            _checkedAppIds.Remove(AppKey(target));
        }

        if (lastResult is null)
            return;
        await RefreshAppsAsync(ct);
        // Leftover scan covers the last uninstalled app (the residual view tracks one app at a time).
        if (lastResult.Removed)
        {
            await ScanResidualAsync(ct);
            if (lastResult.RestartRequired)
                Status += " " + Loc.Get("CleanupRestartRequired");
        }
        else
        {
            Status = Loc.Format("CleanupUninstallPending", lastResult.Detail ?? string.Empty);
        }
    }

    private void RememberUninstall(UninstallTarget target)
    {
        _lastUninstall = target;
        ResidualRows = [];
        _residual.SaveLastUninstall(target);
        ScanResidualCommand.RaiseCanExecuteChanged();
    }

    private async Task ScanResidualAsync(CancellationToken ct)
    {
        if (_lastUninstall is null)
            return;
        Status = Loc.Format("CleanupFindingResiduals", _lastUninstall.App.DisplayName);
        var result = await _residual.ScanAsync(_lastUninstall, ct);
        ResidualRows = result.Items.Select(CleanupItemRow.ForResidual).ToArray();
        ShowResidual = true;
        Status = Loc.Format("CleanupResidualsFound", _lastUninstall.App.DisplayName, ResidualRows.Count);
        if (result.Limited)
            Status += " " + Loc.Get("CleanupResultLimit");
    }

    private async Task RemoveResidualAsync(CancellationToken ct)
    {
        var selected = ResidualRows.Where(row => row.Selected).Select(row => row.Residual!).ToArray();
        if (selected.Length == 0 || _lastUninstall is null)
        {
            Status = Loc.Get("CleanupNothingSelected");
            return;
        }

        if (!Confirm(Loc.Format("CleanupConfirmResidual", _lastUninstall.App.DisplayName, selected.Length)))
            return;

        Status = Loc.Get("CleanupRemoving");
        var result = await _residual.RemoveAsync(_lastUninstall, selected);
        var paths = result.Removed.ToHashSet(StringComparer.OrdinalIgnoreCase);
        ResidualRows = ResidualRows.Where(row => !paths.Contains(row.Path)).ToArray();
        ShowBatchResult(result);
    }

    private static bool Confirm(string message) => MessageBox.Show(Application.Current.MainWindow,
        message, Loc.Get("NavCleanup"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    private void ShowBatchResult(CleanupBatchResult result)
    {
        Status = Loc.Format("CleanupBatchDone", result.Removed.Count, result.Errors.Count);
        if (result.BackupFolder is { } folder)
            Status += " " + Loc.Format("CleanupBackupLocation", folder);
        if (result.Errors.Count > 0)
        {
            var details = string.Join(Environment.NewLine, result.Errors.Take(10));
            MessageBox.Show(Application.Current.MainWindow, Status + Environment.NewLine + Environment.NewLine + details,
                Loc.Get("NavCleanup"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

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

    public string Name => Target.App.DisplayName;
    public string Publisher => Target.App.Publisher;
    public string Version => Target.App.Version;
    public string Type => Target.PackageName is null ? "Win32" : "MSIX / Appx";
    public string Removability => Loc.Get(Target.CanRemove ? "CleanupRemovable" : "CleanupUnavailable");
}
