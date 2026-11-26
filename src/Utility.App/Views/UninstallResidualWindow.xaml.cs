using System.ComponentModel;
using System.Windows;
using Utility.App.Localization;
using Utility.App.ViewModels;
using Utility.Platform.Providers;

namespace Utility.App.Views;

public partial class UninstallResidualWindow : Window
{
    private readonly UninstallResidualService _scanner;
    private readonly UninstallTarget _target;
    private readonly bool _restartRequired;
    private CancellationTokenSource? _scan;
    private bool _closed;
    private bool _removing;
    private ResidualCleanupStatus _cleanupStatus = ResidualCleanupStatus.ReviewOnly;
    private CleanupItemRow[] _rows = [];

    internal UninstallResidualWindow(UninstallResidualService scanner, UninstallTarget target, bool restartRequired)
    {
        _scanner = scanner;
        _target = target;
        _restartRequired = restartRequired;
        InitializeComponent();
        AppName.Text = target.FriendlyName ?? target.App.DisplayName;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await ScanAsync();
    }

    private async void OnRescan(object sender, RoutedEventArgs e) => await ScanAsync();

    private void OnClosing(object? sender, CancelEventArgs e) => e.Cancel = _removing;

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _scan?.Cancel();
    }

    private async Task ScanAsync()
    {
        using var scan = new CancellationTokenSource();
        _scan = scan;
        ShowRows([]);
        SetBusy(true);
        ScanStatus.Text = Loc.Format("CleanupFindingResiduals", _target.App.DisplayName);
        try
        {
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            var result = await _scanner.ScanAsync(_target, scan.Token);
            if (_closed)
                return;

            _cleanupStatus = result.Status;
            var canClean = _cleanupStatus != ResidualCleanupStatus.ReviewOnly;
            var rows = result.Items.Select(item => CleanupItemRow.ForResidual(item, canClean)).ToArray();
            if (canClean)
            {
                foreach (var row in rows)
                    row.Selected = !row.Residual!.ContainsUserData || IncludeUserData.IsChecked == true;
            }
            ShowRows(rows);
            var statusNote = result.Status switch
            {
                ResidualCleanupStatus.ReviewOnly => " " + Loc.Get("CleanupResidualReviewOnly"),
                ResidualCleanupStatus.StaleRegistration => " " + Loc.Get("CleanupStaleRegistration"),
                _ => string.Empty
            };
            ScanStatus.Text = Loc.Format("CleanupResidualsFound", _target.App.DisplayName, rows.Length) + statusNote +
                (result.Limited ? " " + Loc.Get("CleanupResidualLimit") : string.Empty) +
                (_restartRequired ? " " + Loc.Get("CleanupRestartRequired") : string.Empty);
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex)
        {
            if (!_closed)
                ScanStatus.Text = Loc.Format("MsgActionFailed", ex.Message);
        }
        finally
        {
            _scan = null;
            if (!_closed)
                SetBusy(false);
        }
    }

    private void OnUserDataSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_cleanupStatus == ResidualCleanupStatus.ReviewOnly)
            return;
        foreach (var row in _rows.Where(row => row.Residual!.ContainsUserData))
            row.Selected = IncludeUserData.IsChecked == true;
    }

    private async void OnClean(object sender, RoutedEventArgs e)
    {
        var selected = _rows.Where(row => row.Selected).Select(row => row.Residual!).ToArray();
        if (selected.Length == 0)
        {
            ScanStatus.Text = Loc.Get("CleanupNothingSelected");
            return;
        }

        _removing = true;
        SetBusy(true);
        ScanStatus.Text = Loc.Get("CleanupRemoving");
        try
        {
            var result = await _scanner.RemoveAsync(_target, selected);
            var removed = result.Removed.ToHashSet(StringComparer.OrdinalIgnoreCase);
            ShowRows(_rows.Where(row => !removed.Contains(row.Path)).ToArray());
            ScanStatus.Text = Loc.Format("CleanupBatchDone", result.Removed.Count, result.Errors.Count) +
                (result.BackupFolder is { } folder ? " " + Loc.Format("CleanupBackupLocation", folder) : string.Empty);
            if (result.Errors.Count > 0)
            {
                var details = string.Join(Environment.NewLine, result.Errors.Take(10));
                MessageBox.Show(this, details, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            ScanStatus.Text = Loc.Format("MsgActionFailed", ex.Message);
        }
        finally
        {
            _removing = false;
            SetBusy(false);
        }
    }

    private void ShowRows(CleanupItemRow[] rows)
    {
        _rows = rows;
        Rows.ItemsSource = rows;
    }

    private void SetBusy(bool busy)
    {
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        Rescan.IsEnabled = Rows.IsEnabled = !busy;
        var canClean = !busy && _cleanupStatus != ResidualCleanupStatus.ReviewOnly;
        Clean.IsEnabled = canClean && _rows.Length > 0;
        IncludeUserData.IsEnabled = canClean && _rows.Any(row => row.Residual!.ContainsUserData);
        CloseButton.IsEnabled = !_removing;
    }
}
