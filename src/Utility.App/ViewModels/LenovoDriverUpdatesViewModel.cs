using Utility.App.Localization;
using Utility.Core.Models;
using Utility.Platform.Providers;

namespace Utility.App.ViewModels;

public sealed class LenovoDriverUpdatesViewModel : ViewModelBase
{
    private readonly LenovoDriverUpdates _updates = new();
    private LenovoDriverUpdate? _selected;
    private IReadOnlyList<LenovoDriverUpdate> _available = [];
    private string _status = Loc.Get("DriverCatalogHint");
    private string _statusTone = "info";
    private bool _busy;
    private bool _hasPendingCleanup;

    public LenovoDriverUpdatesViewModel()
    {
        ScanCommand = new(() => RunAsync(ScanAsync, "DriverCatalogFailed"), () => !IsBusy);
        InstallCommand = new(() => RunAsync(InstallAsync, "DriverInstallFailed"), () => !IsBusy && Selected is not null);
        CleanupCommand = new(() => RunAsync(CleanupAsync, "DriverCleanupFailed"), () => !IsBusy && HasPendingCleanup);
        HasPendingCleanup = LenovoDriverUpdates.HasPendingCleanup();
    }

    public AsyncRelayCommand ScanCommand { get; }
    public AsyncRelayCommand InstallCommand { get; }
    public AsyncRelayCommand CleanupCommand { get; }
    public IReadOnlyList<LenovoDriverUpdate> Available { get => _available; private set => SetField(ref _available, value); }
    public string Status { get => _status; private set => SetField(ref _status, value); }
    /// <summary>Look of the status bar: "info" (hint), "busy" (working), "ok" (done), "warn" (needs attention).</summary>
    public string StatusTone { get => _statusTone; private set => SetField(ref _statusTone, value); }

    private void SetStatus(string tone, string text)
    {
        StatusTone = tone;
        Status = text;
    }
    public bool IsBusy { get => _busy; private set => SetField(ref _busy, value); }
    public bool HasPendingCleanup
    {
        get => _hasPendingCleanup;
        private set
        {
            if (SetField(ref _hasPendingCleanup, value))
                CleanupCommand.RaiseCanExecuteChanged();
        }
    }
    public LenovoDriverUpdate? Selected
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
                InstallCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task RunAsync(Func<Task> action, string failureKey)
    {
        SetBusy(true);
        try { await action(); }
        catch (Exception ex) { SetStatus("warn", Loc.Format(failureKey, ex.Message)); }
        finally
        {
            HasPendingCleanup = LenovoDriverUpdates.HasPendingCleanup();
            SetBusy(false);
        }
    }

    private async Task ScanAsync()
    {
        Selected = null;
        Available = [];
        SetStatus("busy", Loc.Get("DriverCheckingCatalog"));
        var scan = await _updates.ScanAsync(CancellationToken.None);
        Available = scan.Updates;
        Selected = Available.FirstOrDefault();
        var text = Available.Count == 0
            ? Loc.Format("DriverCatalogUpToDate", scan.ComparedPackages)
            : Loc.Format("DriverCatalogResult", scan.ComparedPackages, Available.Count);
        if (scan.FailedManifests > 0)
            text += " " + Loc.Format("DriverCatalogPartial", scan.FailedManifests);
        // Updates found or something could not be checked → draw attention; otherwise a calm "all good".
        SetStatus(Available.Count > 0 || scan.FailedManifests > 0 ? "warn" : "ok", text);
    }

    private async Task CleanupAsync()
    {
        SetStatus("busy", Loc.Get("DriverCleaningPrevious"));
        var result = await _updates.CleanupAsync();
        if (result.WasCancelled)
            SetStatus("info", Loc.Get("DriverCancelled"));
        else if (result.Success && result.LenovoDriverCleanup is { } cleanup)
            SetStatus(cleanup.PendingPackages == 0 ? "ok" : "warn", cleanup.PendingPackages == 0
                ? Loc.Format("DriverPreviousCleanedAll", cleanup.RemovedPackages)
                : Loc.Format("DriverPreviousCleaned", cleanup.RemovedPackages, cleanup.PendingPackages));
        else
            SetStatus("warn", Loc.Format("DriverCleanupFailed", result.Message ?? result.ErrorCode ?? ""));
    }

    private async Task InstallAsync()
    {
        if (Selected is not { } selected)
            return;
        SetStatus("busy", Loc.Format("DriverInstalling", selected.Title));
        var result = await _updates.InstallAsync(selected, CancellationToken.None);
        if (result.WasCancelled)
        {
            SetStatus("info", Loc.Get("DriverCancelled"));
            return;
        }
        if (result.LenovoDriverInstall is not { } install || !result.Success)
        {
            SetStatus("warn", Loc.Format("DriverInstallFailed", result.Message ?? result.ErrorCode ?? ""));
            return;
        }

        if (install.Active || install.RestartRequired)
        {
            Available = Available.Where(item => item.Id != selected.Id).ToArray();
            Selected = Available.FirstOrDefault();
        }
        if (install.RestartRequired)
            SetStatus("warn", Loc.Get("DriverRestartPending"));
        else if (install.Active)
            SetStatus("ok", install.RetainedPackages == 0
                ? Loc.Format("DriverInstallConfirmedClean", install.RemovedPackages)
                : Loc.Format("DriverInstallConfirmed", install.RemovedPackages, install.RetainedPackages));
        else
            SetStatus("warn", Loc.Get("DriverInstallUnverified"));
    }

    private void SetBusy(bool busy)
    {
        IsBusy = busy;
        ScanCommand.RaiseCanExecuteChanged();
        InstallCommand.RaiseCanExecuteChanged();
        CleanupCommand.RaiseCanExecuteChanged();
    }
}
