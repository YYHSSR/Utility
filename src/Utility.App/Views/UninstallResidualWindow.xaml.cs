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

            var rows = result.Items.Select(CleanupItemRow.ForResidual).ToArray();
            foreach (var row in rows)
                row.Selected = true;
            ShowRows(rows);
            ScanStatus.Text = Loc.Format("CleanupResidualsFound", _target.App.DisplayName, rows.Length) +
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
        Clean.IsEnabled = !busy && _rows.Length > 0;
        CloseButton.IsEnabled = !_removing;
    }
}
