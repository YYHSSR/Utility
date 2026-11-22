using System.Globalization;
using System.Resources;
using System.Windows.Markup;

namespace Utility.App.Localization;

/// <summary>Provides localized UI strings backed by Strings.resx / Strings.zh-CN.resx.</summary>
public static class Loc
{
    private static readonly ResourceManager Manager =
        new("Utility.App.Resources.Strings", typeof(Loc).Assembly);

    public static CultureInfo ActiveCulture { get; set; } = new("zh-CN");

    public static string Get(string key) =>
        Manager.GetString(key, ActiveCulture) ?? key;

    public static string Format(string key, params object[] args) =>
        string.Format(ActiveCulture, Get(key), args);
}

/// <summary>XAML markup extension to resolve localized resource strings cleanly.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension(string key) => Key = key;

    public string Key { get; }

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.Get(Key);
}
