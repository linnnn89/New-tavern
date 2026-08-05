using System.Windows;
using System.Windows.Media;

namespace TavernDesk.App.Services;

public static class InterfaceSettingsRuntime
{
    public const string DefaultFontFamily = "Microsoft YaHei UI";
    public const double DefaultFontSize = 14;
    public const bool DefaultChatAutoScroll = true;
    public const double MinimumFontSize = 10;
    public const double MaximumFontSize = 32;

    public static string FontFamilyName { get; private set; } = DefaultFontFamily;
    public static double FontSize { get; private set; } = DefaultFontSize;
    public static bool ChatAutoScrollEnabled { get; private set; } =
        DefaultChatAutoScroll;

    public static event EventHandler? Changed;

    public static void Apply(
        string? fontFamilyName,
        double fontSize,
        bool chatAutoScrollEnabled)
    {
        FontFamilyName = string.IsNullOrWhiteSpace(fontFamilyName)
            ? DefaultFontFamily
            : fontFamilyName.Trim();
        FontSize = double.IsFinite(fontSize)
            ? Math.Clamp(
                Math.Round(fontSize, MidpointRounding.AwayFromZero),
                MinimumFontSize,
                MaximumFontSize)
            : DefaultFontSize;
        ChatAutoScrollEnabled = chatAutoScrollEnabled;

        if (Application.Current is { } application)
        {
            application.Resources["InterfaceFontFamily"] =
                new FontFamily(FontFamilyName);
            application.Resources["InterfaceFontSize"] = FontSize;
            application.Resources["ChatFontSize"] = FontSize + 1;
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }
}
