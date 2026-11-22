using System.Collections.ObjectModel;
using Utility.Core.Models;

namespace Utility.App.ViewModels;

/// <summary>Editable row representing one power or thermal setting inside Daily or Gaming.</summary>
public sealed class ProfileFieldRow : ViewModelBase
{
    private string _draftText = string.Empty;
    private string _liveReadBackText = "--";
    private string _contractStatusText = string.Empty;

    public required string FieldKey { get; init; }
    public required string DisplayLabel { get; init; }
    public required string Unit { get; init; }
    public required int ReferenceValue { get; init; }

    public string DraftText
    {
        get => _draftText;
        set => SetField(ref _draftText, value);
    }

    public string LiveReadBackText
    {
        get => _liveReadBackText;
        set => SetField(ref _liveReadBackText, value);
    }

    public string ContractStatusText
    {
        get => _contractStatusText;
        set => SetField(ref _contractStatusText, value);
    }
}

/// <summary>
/// One startup entry on the software page. Flipping <see cref="IsEnabled"/> (the row's switch) asks the owner
/// to apply it; the switch stays disabled while that runs and snaps back if it fails or is cancelled.
/// </summary>
public sealed class StartupEntryRow : ViewModelBase
{
    private readonly Func<StartupEntryRow, bool, Task<bool>> _apply;
    private bool _isEnabled;
    private bool _isBusy;

    public StartupEntryRow(StartupEntry entry, Func<StartupEntryRow, bool, Task<bool>> apply)
    {
        Entry = entry;
        _apply = apply;
        _isEnabled = entry.IsEnabled ?? true;
    }

    public StartupEntry Entry { get; }
    public string Name => Entry.Name;
    public StartupEntrySource Source => Entry.Source;
    /// <summary>Program path; built-in task actions (COM handlers etc.) get a readable label instead.</summary>
    public string CommandOrTarget => Entry.CommandOrTarget == "(non-exec action)"
        ? Utility.App.Localization.Loc.Get("StartupNonExecTarget")
        : Entry.CommandOrTarget;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
                RaisePropertyChanged(nameof(IsIdle));
        }
    }

    public bool IsIdle => !_isBusy;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isBusy || value == _isEnabled)
                return;
            var previous = _isEnabled;
            SetField(ref _isEnabled, value);
            _ = ApplyAsync(previous, value);
        }
    }

    private async Task ApplyAsync(bool previous, bool requested)
    {
        IsBusy = true;
        var ok = false;
        try
        {
            ok = await _apply(this, requested);
        }
        catch (Exception)
        {
            ok = false;
        }
        finally
        {
            IsBusy = false;
            if (!ok)
            {
                _isEnabled = previous;
                RaisePropertyChanged(nameof(IsEnabled));
            }
        }
    }
}

/// <summary>Editable point row for custom fan curve preview and validation.</summary>
public sealed class FanPointRow : ViewModelBase
{
    private int _temperatureC;
    private int _fanSpeedValue;

    public int TemperatureC
    {
        get => _temperatureC;
        set => SetField(ref _temperatureC, value);
    }

    public int FanSpeedValue
    {
        get => _fanSpeedValue;
        set => SetField(ref _fanSpeedValue, value);
    }

}


/// <summary>An editable, manually captured curve. Firmware bounds are unknown in this draft.</summary>
public sealed class FanChannelDraft : ViewModelBase
{
    private string _name = string.Empty;
    private string _sensorName = string.Empty;
    private string _speedUnit = "RPM";

    public int Id { get; init; }
    public string Name { get => _name; set => SetField(ref _name, value); }
    public string SensorName { get => _sensorName; set => SetField(ref _sensorName, value); }
    public string SpeedUnit { get => _speedUnit; set => SetField(ref _speedUnit, value); }

    public ObservableCollection<FanPointRow> Points { get; } = [];

    public FanChannelCurve ToDraftCurve() => new(
        Id, Name.Trim(), SensorName.Trim(),
        Points.Select(p => new FanCurvePoint(p.TemperatureC, p.FanSpeedValue, SpeedUnit)).ToArray(),
        0, 125, 1, 0, SpeedUnit == "%" ? 100 : int.MaxValue, 1, SpeedUnit);

    public static FanChannelDraft FromSaved(FanChannelCurve channel)
    {
        var draft = new FanChannelDraft
        {
            Id = channel.ChannelId,
            Name = channel.ChannelName,
            SensorName = channel.SensorName,
            SpeedUnit = channel.SpeedUnit
        };
        foreach (var point in channel.Points)
        {
            draft.Points.Add(new FanPointRow { TemperatureC = point.TemperatureC, FanSpeedValue = point.FanSpeedValue });
        }

        return draft;
    }
}
