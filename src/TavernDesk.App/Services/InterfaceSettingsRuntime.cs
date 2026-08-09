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
    public const int DefaultScalePercent = 100;
    public const int MinimumScalePercent = 80;
    public const int MaximumScalePercent = 150;

    public static string FontFamilyName { get; private set; } = DefaultFontFamily;
    public static double FontSize { get; private set; } = DefaultFontSize;
    public static bool ChatAutoScrollEnabled { get; private set; } =
        DefaultChatAutoScroll;
    public static int ScalePercent { get; private set; } = DefaultScalePercent;
    public static double ScaleFactor => ScalePercent / 100d;

    public static event EventHandler? Changed;

    public static void Apply(
        string? fontFamilyName,
        double fontSize,
        bool chatAutoScrollEnabled,
        int scalePercent = DefaultScalePercent)
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
        ScalePercent = NormalizeScalePercent(scalePercent);

        if (Application.Current is { } application)
        {
            application.Resources["InterfaceFontFamily"] =
                new FontFamily(FontFamilyName);
            application.Resources["InterfaceFontSize"] = FontSize;
            application.Resources["ChatFontSize"] = FontSize + 1;
            ApplyScaleResource(application);
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void ApplyScale(int scalePercent)
    {
        ScalePercent = NormalizeScalePercent(scalePercent);
        if (Application.Current is { } application)
        {
            ApplyScaleResource(application);
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static int NormalizeScalePercent(double scalePercent) =>
        double.IsFinite(scalePercent)
            ? Math.Clamp(
                (int)Math.Round(scalePercent, MidpointRounding.AwayFromZero),
                MinimumScalePercent,
                MaximumScalePercent)
            : DefaultScalePercent;

    private static void ApplyScaleResource(Application application)
    {
        var transform = new ScaleTransform(ScaleFactor, ScaleFactor);
        transform.Freeze();
        application.Resources["InterfaceScaleTransform"] = transform;
    }
}
