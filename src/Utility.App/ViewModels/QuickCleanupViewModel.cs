using System.IO;
using System.Windows;
using Utility.App.Localization;
using Utility.Core.Models;
using Utility.Platform.Lenovo;
using Utility.Platform.Native;
using Utility.Platform.Providers;

namespace Utility.App.ViewModels;

/// <summary>
/// 垃圾清理 page: one-click cleanup frees memory and temporary/cache files automatically, then shows
/// C-drive results (incl. System Restore points), duplicate files, and per-drive large files
/// (each drive is scanned only after its switch is turned on).
/// </summary>
internal sealed class QuickCleanupViewModel : ViewModelBase, IDisposable
{
    internal const long LargeFileMinimumBytes = 256L * 1024 * 1024;

    private readonly DiskCleanupService _disk = new();
    private readonly CancellationTokenSource _lifetime = new();
    private bool _isCleaning;
    private bool _hasRun;
    private string _page = "system";
    private bool _isRestoreBusy;
    private bool _isDuplicateBusy;
    private string _memoryText = string.Empty;
    private string _cacheText = string.Empty;
    private string _tempSummary = string.Empty;
    private string _restoreSummary = string.Empty;
    private string _duplicateSummary = string.Empty;
    private IReadOnlyList<CleanupItemRow> _tempRows = [];
    private IReadOnlyList<RestorePointRow> _restorePoints = [];
    private IReadOnlyList<CleanupItemRow> _duplicateRows = [];

    public QuickCleanupViewModel()
    {
        QuickCleanupCommand = new AsyncRelayCommand(() => GuardAsync(RunAsync), () => !IsCleaning && !IsDuplicateBusy);
        ChangePageCommand = new RelayCommand(parameter =>
        {
            if (parameter is string page && page != _page)
            {
                _page = page;
                RaisePropertyChanged(nameof(Page));
            }
        });
        ScanDuplicatesCommand = new AsyncRelayCommand(() => GuardAsync(ScanDuplicatesAsync), () => !IsDuplicateBusy && !IsCleaning);
        LoadRestorePointsCommand = new AsyncRelayCommand(() => GuardAsync(LoadRestorePointsAsync), () => !IsRestoreBusy);
        DeleteRestorePointsCommand = new AsyncRelayCommand(() => GuardAsync(DeleteRestorePointsAsync),
            () => !IsRestoreBusy && RestorePoints.Count > 0);
        SelectDuplicateCopiesCommand = new RelayCommand(SelectDuplicateCopies, () => !IsDuplicateBusy && DuplicateRows.Count > 0);
        DeleteDuplicatesCommand = new AsyncRelayCommand(() => GuardAsync(DeleteDuplicatesAsync),
            () => !IsDuplicateBusy && DuplicateRows.Count > 0);

    }

    public async Task LoadDrivesAsync()
    {
        var ct = _lifetime.Token;
        var drives = await Task.Run(() =>
        {
            var available = new List<DriveScanViewModel>();
            foreach (var drive in DriveInfo.GetDrives())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (drive.DriveType == DriveType.Fixed && drive.IsReady)
                        available.Add(new DriveScanViewModel(drive, _disk, ct));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A removable fixed volume may disappear between enumeration and querying capacity.
                }
            }
            return available;
        }, ct);
        if (!ct.IsCancellationRequested)
        {
            Drives = drives;
            RaisePropertyChanged(nameof(Drives));
        }
    }

    public AsyncRelayCommand QuickCleanupCommand { get; }
    public RelayCommand ChangePageCommand { get; }
    public AsyncRelayCommand ScanDuplicatesCommand { get; }
    public AsyncRelayCommand LoadRestorePointsCommand { get; }

    /// <summary>Visible tab: "system" (C 盘清理), "duplicate" or "large".</summary>
    public string Page => _page;

    /// <summary>True once one-click cleanup has run (or is running) in this session.</summary>
    public bool HasRun { get => _hasRun; private set => SetField(ref _hasRun, value); }
    public AsyncRelayCommand DeleteRestorePointsCommand { get; }
    public RelayCommand SelectDuplicateCopiesCommand { get; }
    public AsyncRelayCommand DeleteDuplicatesCommand { get; }
    public IReadOnlyList<DriveScanViewModel> Drives { get; private set; } = [];
    public string LargeThresholdText => Loc.Format("QcLargeThreshold", CleanupViewModel.FormatSize(LargeFileMinimumBytes));

    /// <summary>Raised once the automatic part finishes, with a one-line summary (for the tray / toast).</summary>
    public event Action<string>? Completed;

    public bool IsCleaning
    {
        get => _isCleaning;
        private set
        {
            if (!SetField(ref _isCleaning, value))
                return;
            QuickCleanupCommand.RaiseCanExecuteChanged();
            ScanDuplicatesCommand.RaiseCanExecuteChanged();
        }
    }
    public string MemoryText { get => _memoryText; private set => SetField(ref _memoryText, value); }
    public string CacheText { get => _cacheText; private set => SetField(ref _cacheText, value); }
    public string TempSummary { get => _tempSummary; private set => SetField(ref _tempSummary, value); }
    public string RestoreSummary { get => _restoreSummary; private set => SetField(ref _restoreSummary, value); }
    public string DuplicateSummary { get => _duplicateSummary; private set => SetField(ref _duplicateSummary, value); }
    public IReadOnlyList<CleanupItemRow> TempRows { get => _tempRows; private set => SetField(ref _tempRows, value); }

    public IReadOnlyList<RestorePointRow> RestorePoints
    {
        get => _restorePoints;
        private set
        {
            if (SetField(ref _restorePoints, value))
                DeleteRestorePointsCommand.RaiseCanExecuteChanged();
        }
    }

    public IReadOnlyList<CleanupItemRow> DuplicateRows
    {
        get => _duplicateRows;
        private set
        {
            if (!SetField(ref _duplicateRows, value))
                return;
            SelectDuplicateCopiesCommand.RaiseCanExecuteChanged();
            DeleteDuplicatesCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsRestoreBusy
    {
        get => _isRestoreBusy;
        private set
        {
            if (!SetField(ref _isRestoreBusy, value))
                return;
            LoadRestorePointsCommand.RaiseCanExecuteChanged();
            DeleteRestorePointsCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsDuplicateBusy
    {
        get => _isDuplicateBusy;
        private set
        {
            if (!SetField(ref _isDuplicateBusy, value))
                return;
            QuickCleanupCommand.RaiseCanExecuteChanged();
            SelectDuplicateCopiesCommand.RaiseCanExecuteChanged();
            DeleteDuplicatesCommand.RaiseCanExecuteChanged();
            ScanDuplicatesCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Reclaim memory while one scan looks for both caches and duplicate files.</summary>
    public async Task RunAsync()
    {
        if (IsCleaning || IsDuplicateBusy)
            return;
        var ct = _lifetime.Token;
        IsCleaning = true;
        HasRun = true;
        ChangePageCommand.Execute("system");
        MemoryText = Loc.Get("QcWorking");
        CacheText = Loc.Get("QcWorking");
        try
        {
            await Task.WhenAll(CleanMemoryAsync(ct), ScanSystemDriveAsync(ct));
        }
        finally
        {
            IsCleaning = false;
        }

        if (!ct.IsCancellationRequested)
            Completed?.Invoke($"{Loc.Get("QcMemory")}：{MemoryText} {Loc.Get("QcCache")}：{CacheText}");
    }

    private async Task CleanMemoryAsync(CancellationToken ct)
    {
        try
        {
            var result = await Task.Run(MemoryReclaim.RunAsync, ct);
            MemoryText = result.TrimmedProcesses == 0
                ? Loc.Get("MsgMemoryNothingToReclaim")
                : result.AvailableBeforeGiB is double before && result.AvailableAfterGiB is double after
                    ? Loc.Format("MsgMemoryReclaimed", result.TrimmedProcesses, before, after)
                    : Loc.Format("MsgMemoryReclaimedCount", result.TrimmedProcesses);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            MemoryText = Loc.Format("MsgActionFailed", ex.Message);
        }
    }

    private async Task ScanSystemDriveAsync(CancellationToken ct)
    {
        IsDuplicateBusy = true;
        try
        {
            var scan = await _disk.ScanSystemDriveAsync(DuplicateProgress(), ct);
            await Task.WhenAll(CleanCacheAsync(scan.Temporary, ct), scan.Duplicate);
            ApplyDuplicateScan(await scan.Duplicate);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CacheText = Loc.Format("MsgActionFailed", ex.Message);
            TempSummary = CacheText;
            DuplicateSummary = CacheText;
        }
        finally
        {
            IsDuplicateBusy = false;
        }
    }

    private async Task CleanCacheAsync(DiskScanResult scan, CancellationToken ct)
    {
        try
        {
            var result = await _disk.RemoveAsync(DiskScanKind.Temporary, scan.Files, scan.Files, ct);
            var removed = result.Removed.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var removedBytes = scan.Files.Where(file => removed.Contains(file.Path)).Sum(file => file.Bytes);
            var inUse = scan.Files.Count - removed.Count;
            var removedLabel = Loc.Get("QcStatusRemoved");
            var inUseLabel = Loc.Get("QcStatusInUse");
            TempRows = scan.Files
                .Select(file => CleanupItemRow.WithNote(file, removed.Contains(file.Path) ? removedLabel : inUseLabel))
                .ToArray();
            CacheText = Loc.Format("QcCacheResult", removed.Count, CleanupViewModel.FormatSize(removedBytes));
            TempSummary = scan.Files.Count == 0
                ? Loc.Get("QcTempNone")
                : Loc.Format("QcTempSummary", removed.Count, CleanupViewModel.FormatSize(removedBytes), inUse);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CacheText = Loc.Format("MsgActionFailed", ex.Message);
            TempSummary = CacheText;
        }
    }

    private async Task ScanDuplicatesAsync()
    {
        var ct = _lifetime.Token;
        IsDuplicateBusy = true;
        try
        {
            var result = await _disk.ScanAsync(DiskScanKind.Duplicate, _disk.SystemDrivePath, 0, DuplicateProgress(), ct);
            ApplyDuplicateScan(result);
        }
        finally
        {
            IsDuplicateBusy = false;
        }
    }

    private IProgress<long> DuplicateProgress()
    {
        DuplicateSummary = Loc.Format("QcScanning", 0);
        return new Progress<long>(count =>
        {
            if (IsDuplicateBusy)
                DuplicateSummary = Loc.Format("QcScanning", count);
        });
    }

    private void ApplyDuplicateScan(DiskScanResult result)
    {
        DuplicateRows = result.Files.Select(CleanupItemRow.ForDisk).ToArray();
        UpdateDuplicateSummary();
    }

    private void UpdateDuplicateSummary()
    {
        var groups = DuplicateRows.GroupBy(row => row.File!.Group).Where(group => group.Count() > 1).ToArray();
        var reclaimable = groups.Sum(group => group.Skip(1).Sum(row => row.File!.Bytes));
        DuplicateSummary = groups.Length == 0
            ? Loc.Get("QcDuplicateNone")
            : Loc.Format("QcDuplicateSummary", groups.Length, CleanupViewModel.FormatSize(reclaimable));
    }

    private void SelectDuplicateCopies()
    {
        foreach (var group in DuplicateRows.GroupBy(row => row.File!.Group))
        {
            var first = true;
            foreach (var row in group)
            {
                row.Selected = !first;
                first = false;
            }
        }
    }

    private async Task DeleteDuplicatesAsync()
    {
        var selected = DuplicateRows.Where(row => row.Selected).Select(row => row.File!).ToArray();
        if (selected.Length == 0)
        {
            DuplicateSummary = Loc.Get("CleanupNothingSelected");
            return;
        }
        if (!Confirm(Loc.Format("CleanupConfirmFiles", selected.Length)))
            return;

        IsDuplicateBusy = true;
        try
        {
            var result = await _disk.RemoveAsync(DiskScanKind.Duplicate, DuplicateRows.Select(row => row.File!).ToArray(), selected);
            var removed = result.Removed.ToHashSet(StringComparer.OrdinalIgnoreCase);
            DuplicateRows = DuplicateRows.Where(row => !removed.Contains(row.Path)).ToArray();
            UpdateDuplicateSummary();
            DuplicateSummary = Loc.Format("CleanupBatchDone", result.Removed.Count, result.Errors.Count) + " " + DuplicateSummary;
        }
        finally
        {
            IsDuplicateBusy = false;
        }
    }

    // ---------------- System Restore (administrator, via the elevated worker) ----------------

    private async Task LoadRestorePointsAsync()
    {
        IsRestoreBusy = true;
        try
        {
            RestoreSummary = Loc.Get("QcRestoreLoading");
            var response = await new ElevatedWorkerClient().ExecuteRequestAsync(new ElevatedRequest
            {
                AuthToken = string.Empty,
                Operation = ElevatedOperation.ListRestorePoints
            }, _lifetime.Token);
            ApplyRestoreResponse(response, deleted: null);
        }
        finally
        {
            IsRestoreBusy = false;
        }
    }

    private async Task DeleteRestorePointsAsync()
    {
        var selected = RestorePoints.Where(row => row.Selected).Select(row => row.SequenceNumber).ToArray();
        if (selected.Length == 0)
        {
            RestoreSummary = Loc.Get("CleanupNothingSelected");
            return;
        }
        if (!Confirm(Loc.Format("QcRestoreConfirm", selected.Length)))
            return;

        IsRestoreBusy = true;
        try
        {
            var before = RestorePoints.Count;
            var response = await new ElevatedWorkerClient().ExecuteRequestAsync(new ElevatedRequest
            {
                AuthToken = string.Empty,
                Operation = ElevatedOperation.DeleteRestorePoints,
                RestorePointSequenceNumbers = selected
            }, _lifetime.Token);
            ApplyRestoreResponse(response, deleted: response.RestorePoints is { } list ? before - list.Points.Count : 0);
        }
        finally
        {
            IsRestoreBusy = false;
        }
    }

    private void ApplyRestoreResponse(ElevatedResponse response, int? deleted)
    {
        if (response.WasCancelled)
        {
            RestoreSummary = Loc.Get("QcRestoreCancelled");
            return;
        }

        if (response.RestorePoints is { } list)
        {
            RestorePoints = list.Points.Select(point => new RestorePointRow(point)).ToArray();
            var summary = list.Points.Count == 0
                ? Loc.Get("QcRestoreNone")
                : list.UsedBytes is long used
                    ? Loc.Format("QcRestoreSummary", list.Points.Count, CleanupViewModel.FormatSize(used))
                    : Loc.Format("QcRestoreSummaryNoSize", list.Points.Count);
            if (deleted is int count)
                summary = Loc.Format("QcRestoreDeleted", count) + " " + summary;
            if (!response.Success && response.Message is { } message)
                summary += " " + Loc.Format("QcRestoreFailed", message);
            RestoreSummary = summary;
            return;
        }

        RestoreSummary = Loc.Format("QcRestoreFailed", response.Message ?? response.ErrorCode ?? string.Empty);
    }

    private async Task GuardAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            MessageBox.Show(Application.Current.MainWindow, Loc.Format("MsgActionFailed", ex.Message), Loc.Get("QcTitle"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    internal static bool Confirm(string message) => MessageBox.Show(ActiveWindow(),
        message, Loc.Get("QcTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    private static Window ActiveWindow() =>
        Application.Current.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive) ?? Application.Current.MainWindow;

    public void Dispose()
    {
        _lifetime.Cancel();
        foreach (var drive in Drives)
            drive.Cancel();
    }
}

/// <summary>One restore point row with a selection checkbox.</summary>
internal sealed class RestorePointRow(RestorePointInfo point) : ViewModelBase
{
    private bool _selected;
    public uint SequenceNumber => point.SequenceNumber;
    public string Description => string.IsNullOrWhiteSpace(point.Description) ? "—" : point.Description;
    public string Created => point.CreatedUtc == DateTime.MinValue ? "—" : point.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public bool Selected { get => _selected; set => SetField(ref _selected, value); }
}

/// <summary>
/// Large-file scan for one fixed drive. Turning <see cref="IsEnabled"/> on starts the scan;
/// turning it off cancels it and clears the list.
/// </summary>
internal sealed class DriveScanViewModel : ViewModelBase
{
    private readonly DiskCleanupService _disk;
    private readonly CancellationToken _lifetime;
    private CancellationTokenSource? _scan;
    private bool _isEnabled;
    private bool _isBusy;
    private string _summary = string.Empty;
    private IReadOnlyList<CleanupItemRow> _rows = [];

    public DriveScanViewModel(DriveInfo drive, DiskCleanupService disk, CancellationToken lifetime)
    {
        _disk = disk;
        _lifetime = lifetime;
        Root = drive.RootDirectory.FullName;
        var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? Loc.Get("QcLocalDisk") : drive.VolumeLabel;
        Title = $"{Root.TrimEnd('\\')}  {label}";
        UsedPercent = drive.TotalSize > 0 ? (drive.TotalSize - drive.TotalFreeSpace) * 100d / drive.TotalSize : 0;
        UsageText = Loc.Format("QcDriveUsage", CleanupViewModel.FormatSize(drive.TotalSize - drive.TotalFreeSpace),
            CleanupViewModel.FormatSize(drive.TotalSize));
        DeleteSelectedCommand = new AsyncRelayCommand(DeleteSelectedAsync, () => !IsBusy && Rows.Count > 0);
        SelectAllCommand = new RelayCommand(() => { foreach (var row in Rows) row.Selected = true; }, () => !IsBusy && Rows.Count > 0);
    }

    public string Root { get; }
    public string Title { get; }
    public string UsageText { get; }
    public double UsedPercent { get; }
    public AsyncRelayCommand DeleteSelectedCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public string Summary { get => _summary; private set => SetField(ref _summary, value); }

    public IReadOnlyList<CleanupItemRow> Rows
    {
        get => _rows;
        private set
        {
            if (!SetField(ref _rows, value))
                return;
            DeleteSelectedCommand.RaiseCanExecuteChanged();
            SelectAllCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetField(ref _isBusy, value))
                return;
            DeleteSelectedCommand.RaiseCanExecuteChanged();
            SelectAllCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (!SetField(ref _isEnabled, value))
                return;
            if (value)
                _ = ScanAsync();
            else
            {
                Cancel();
                Rows = [];
                Summary = string.Empty;
            }
        }
    }

    public void Cancel() => _scan?.Cancel();

    private async Task ScanAsync()
    {
        Cancel();
        using var scan = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        _scan = scan;
        IsBusy = true;
        Rows = [];
        try
        {
            Summary = Loc.Format("QcScanning", 0);
            var progress = new Progress<long>(count =>
            {
                if (!scan.IsCancellationRequested)
                    Summary = Loc.Format("QcScanning", count);
            });
            var result = await _disk.ScanAsync(DiskScanKind.Large, Root, QuickCleanupViewModel.LargeFileMinimumBytes, progress, scan.Token);
            scan.Token.ThrowIfCancellationRequested();
            Rows = result.Files.Select(CleanupItemRow.ForDisk).ToArray();
            UpdateSummary();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_scan, scan) && !scan.IsCancellationRequested)
                Summary = Loc.Format("MsgActionFailed", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_scan, scan))
            {
                _scan = null;
                IsBusy = false;
            }
        }
    }

    private void UpdateSummary() => Summary = Rows.Count == 0
        ? Loc.Format("QcLargeNone", CleanupViewModel.FormatSize(QuickCleanupViewModel.LargeFileMinimumBytes))
        : Loc.Format("QcLargeSummary", Rows.Count, CleanupViewModel.FormatSize(Rows.Sum(row => row.File!.Bytes)));

    private async Task DeleteSelectedAsync()
    {
        var selected = Rows.Where(row => row.Selected).Select(row => row.File!).ToArray();
        if (selected.Length == 0)
        {
            Summary = Loc.Get("CleanupNothingSelected");
            return;
        }
        if (!QuickCleanupViewModel.Confirm(Loc.Format("CleanupConfirmFiles", selected.Length)))
            return;

        IsBusy = true;
        try
        {
            var result = await _disk.RemoveAsync(DiskScanKind.Large, Rows.Select(row => row.File!).ToArray(), selected);
            var removed = result.Removed.ToHashSet(StringComparer.OrdinalIgnoreCase);
            Rows = Rows.Where(row => !removed.Contains(row.Path)).ToArray();
            UpdateSummary();
            Summary = Loc.Format("CleanupBatchDone", result.Removed.Count, result.Errors.Count) + " " + Summary;
        }
        catch (Exception ex)
        {
            Summary = Loc.Format("MsgActionFailed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
