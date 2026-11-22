using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Utility.App.Controls;

/// <summary>True when the bound value equals ConverterParameter (string compare). One-way only.</summary>
public sealed class EqualsToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Visible when the bound value equals ConverterParameter.</summary>
public sealed class EqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Collapsed for null, empty strings and zero counts.</summary>
public sealed class EmptyToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var empty = value switch
        {
            null => true,
            string s => string.IsNullOrWhiteSpace(s),
            int i => i == 0,
            _ => false
        };
        if (parameter is "invert")
            empty = !empty;
        return empty ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Formats a 0..100 double (NaN = unavailable) as an integer, or "--".</summary>
public sealed class PercentTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double d && double.IsFinite(d) ? $"{Math.Round(d).ToString(culture)}%" : "--";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>NaN / null become 0 so RangeBase controls never receive an invalid value.</summary>
public sealed class NanToZeroConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double d && !double.IsNaN(d) && !double.IsInfinity(d) ? d : 0d;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Maps a boolean to one of two strings given as "trueText|falseText".</summary>
public sealed class BoolToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var parts = (parameter as string ?? "|").Split('|');
        return value is true ? parts[0] : parts.ElementAtOrDefault(1) ?? string.Empty;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>
/// Shows an enum value via the string resource "{ConverterParameter}{Value}" (e.g. DriverCategoryGraphics);
/// falls back to the raw name when no resource exists. One-way only.
/// </summary>
public sealed class LocalizedEnumConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null)
            return string.Empty;
        var key = $"{parameter}{value}";
        var text = Utility.App.Localization.Loc.Get(key);
        return text == key ? value.ToString() ?? string.Empty : text;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Collapsed when true, visible when false.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
