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

    public string SpeedUnit { get; set; } = "RPM";
}
