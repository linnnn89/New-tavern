using System.Runtime.InteropServices;
using System.Windows;
using TavernDesk.App.Localization;

namespace TavernDesk.App.Services;

public sealed class InterfaceScaleRecommendationProvider : IInterfaceScaleRecommendationProvider
{
    private readonly Func<DisplayMetrics> _metrics;

    public InterfaceScaleRecommendationProvider(Func<DisplayMetrics>? metrics = null)
    {
        _metrics = metrics ?? ReadPrimaryDisplayMetrics;
    }

    public InterfaceScaleRecommendation? GetRecommendation()
    {
        try
        {
            return Recommend(_metrics());
        }
        catch
        {
            return null;
        }
    }

    public static InterfaceScaleRecommendation Recommend(DisplayMetrics metrics)
    {
        var percent = RecommendPercent(metrics);
        var reason = percent == InterfaceSettingsRuntime.DefaultScalePercent
            ? LanguageRuntime.GetString("Settings.ScaleRecommendation.Reason.Standard")
            : LanguageRuntime.GetString("Settings.ScaleRecommendation.Reason.LargeWorkspace");
        if (OsScale(metrics.Dpi) >= 1.2
            && percent <= 110)
        {
            reason = LanguageRuntime.GetString(
                "Settings.ScaleRecommendation.Reason.OsScaled");
        }

        return new InterfaceScaleRecommendation(percent, reason);
    }

    public static int RecommendPercent(DisplayMetrics metrics)
    {
        var osScale = OsScale(metrics.Dpi);
        var width = metrics.WorkAreaWidth;
        var height = metrics.WorkAreaHeight;

        if (osScale >= 1.45)
        {
            return InterfaceSettingsRuntime.DefaultScalePercent;
        }

        if (osScale >= 1.2)
        {
            return height >= 1600 || width >= 2800
                ? 110
                : InterfaceSettingsRuntime.DefaultScalePercent;
        }

        if (height >= 1800 || width >= 3200)
        {
            return 150;
        }

        if (height >= 1440 || width >= 2560)
        {
            return 125;
        }

        if (height >= 1200 || width >= 2200)
        {
            return 110;
        }

        return InterfaceSettingsRuntime.DefaultScalePercent;
    }

    private static double OsScale(int dpi) =>
        (dpi <= 0 ? 96d : dpi) / 96d;

    private static DisplayMetrics ReadPrimaryDisplayMetrics() =>
        new(
            NativeDisplayDpi.GetSystemDpi(),
            SystemParameters.WorkArea.Width,
            SystemParameters.WorkArea.Height);
}

internal static class NativeDisplayDpi
{
    public static int GetSystemDpi()
    {
        try
        {
            var dpi = GetDpiForSystem();
            return dpi == 0 ? 96 : (int)dpi;
        }
        catch
        {
            return 96;
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();
}
