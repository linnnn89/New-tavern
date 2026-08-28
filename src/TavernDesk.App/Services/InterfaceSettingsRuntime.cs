using System.Windows;
using System.Windows.Media;

namespace TavernDesk.App.Services;

public static class InterfaceSettingsRuntime
{
    public const string LightThemeName = "light";
    public const string DarkThemeName = "dark";
    public const string DefaultThemeName = LightThemeName;
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
    public static string ThemeName { get; private set; } = DefaultThemeName;

    private static readonly IReadOnlyDictionary<string, string> LightThemeBrushes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["WindowBackgroundBrush"] = "#F6F8FC",
            ["SurfaceBrush"] = "#FFFFFFFF",
            ["SurfaceSolidBrush"] = "#FFFFFFFF",
            ["SurfaceAltBrush"] = "#F1F4F8",
            ["BorderBrush"] = "#D9E1EC",
            ["TextBrush"] = "#182230",
            ["MutedTextBrush"] = "#667085",
            ["AccentBrush"] = "#2563EB",
            ["AccentSoftBrush"] = "#EAF2FF",
            ["ControlHoverBrush"] = "#F2F6FC",
            ["ControlPressedBrush"] = "#E5ECF6",
            ["ControlDisabledBrush"] = "#F2F4F7",
            ["FocusRingBrush"] = "#84ADFF",
            ["ScrollThumbBrush"] = "#B7C2D1",
            ["ScrollThumbHoverBrush"] = "#7D8DA2",
            ["SuccessBrush"] = "#178A60",
            ["WarningBrush"] = "#B45309",
            ["DangerBrush"] = "#D92D20",
            ["MessagePlusBrush"] = "#748096",
            ["InteractionOverlayBrush"] = "#1F2937",
            ["AppicaSelectedBorderBrush"] = "#AFC9F8",
            ["AppicaPanelBorderBrush"] = "#DDE3EC",
            ["AppicaShellCanvasBrush"] = "#F7F9FC",
            ["AppicaShellSurfaceBrush"] = "#FFFFFFFF",
            ["AppicaShellDividerBrush"] = "#E5E9F0",
            ["AppicaShellTextBrush"] = "#171B24",
            ["AppicaShellMutedBrush"] = "#697386",
            ["AppicaShellSubtleBrush"] = "#8A94A6",
            ["AppicaShellAccentBrush"] = "#0F6BEE",
            ["AppicaShellAccentHoverBrush"] = "#075FDC",
            ["AppicaShellAccentSoftBrush"] = "#E8F1FF",
            ["AppicaShellAccentBorderBrush"] = "#CFE0FF",
            ["AppicaShellHoverBrush"] = "#F3F6FA",
            ["AppicaShellPressedBrush"] = "#E9EEF5",
            ["AppicaShellStatusSurfaceBrush"] = "#F7F9FC",
            ["AppicaShellSuccessBrush"] = "#1BAA67",
            ["AppicaShellDangerBrush"] = "#B42318",
            ["AppicaShellDangerSurfaceBrush"] = "#FFF2F0",
            ["AppicaShellDangerHoverBrush"] = "#FFE8E5",
            ["AppicaShellDangerBorderBrush"] = "#F6C8C2",
            ["AppicaShellFocusBrush"] = "#79A7FF",
            ["AppicaDashboardHeroBrush"] = "#EDF4FF",
            ["AppicaDashboardHeroBorderBrush"] = "#D8E6FF",
            ["AppicaDashboardPurpleSoftBrush"] = "#F1ECFF",
            ["AppicaDashboardPurpleBrush"] = "#7450D8",
            ["AppicaDashboardGreenSoftBrush"] = "#E8F8F0",
            ["AppicaDashboardGreenBrush"] = "#16845B"
        };

    private static readonly IReadOnlyDictionary<string, string> DarkThemeBrushes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["WindowBackgroundBrush"] = "#1A1D24",
            ["SurfaceBrush"] = "#22262F",
            ["SurfaceSolidBrush"] = "#1F232B",
            ["SurfaceAltBrush"] = "#2A2F39",
            ["BorderBrush"] = "#2D333F",
            ["TextBrush"] = "#E2E8F0",
            ["MutedTextBrush"] = "#94A3B8",
            ["AccentBrush"] = "#2F8EE5",
            ["AccentSoftBrush"] = "#183A57",
            ["ControlHoverBrush"] = "#2B3038",
            ["ControlPressedBrush"] = "#343A44",
            ["ControlDisabledBrush"] = "#24272D",
            ["FocusRingBrush"] = "#69B7FF",
            ["ScrollThumbBrush"] = "#555C68",
            ["ScrollThumbHoverBrush"] = "#737D8D",
            ["SuccessBrush"] = "#55D982",
            ["WarningBrush"] = "#E8B86D",
            ["DangerBrush"] = "#FF7772",
            ["MessagePlusBrush"] = "#9AA3B5",
            ["InteractionOverlayBrush"] = "#FFFFFFFF",
            ["AppicaSelectedBorderBrush"] = "#3C78A8",
            ["AppicaPanelBorderBrush"] = "#343943",
            ["AppicaShellCanvasBrush"] = "#1A1D24",
            ["AppicaShellSurfaceBrush"] = "#1F232B",
            ["AppicaShellDividerBrush"] = "#2D333F",
            ["AppicaShellTextBrush"] = "#E2E8F0",
            ["AppicaShellMutedBrush"] = "#94A3B8",
            ["AppicaShellSubtleBrush"] = "#7E8796",
            ["AppicaShellAccentBrush"] = "#3297F0",
            ["AppicaShellAccentHoverBrush"] = "#56ACF6",
            ["AppicaShellAccentSoftBrush"] = "#193A58",
            ["AppicaShellAccentBorderBrush"] = "#2C5E87",
            ["AppicaShellHoverBrush"] = "#272B32",
            ["AppicaShellPressedBrush"] = "#30353D",
            ["AppicaShellStatusSurfaceBrush"] = "#191B20",
            ["AppicaShellSuccessBrush"] = "#55D982",
            ["AppicaShellDangerBrush"] = "#FF7772",
            ["AppicaShellDangerSurfaceBrush"] = "#3A2023",
            ["AppicaShellDangerHoverBrush"] = "#4A2529",
            ["AppicaShellDangerBorderBrush"] = "#6C343A",
            ["AppicaShellFocusBrush"] = "#69B7FF",
            ["AppicaDashboardHeroBrush"] = "#192B3E",
            ["AppicaDashboardHeroBorderBrush"] = "#285277",
            ["AppicaDashboardPurpleSoftBrush"] = "#2A2440",
            ["AppicaDashboardPurpleBrush"] = "#A88BFF",
            ["AppicaDashboardGreenSoftBrush"] = "#17362B",
            ["AppicaDashboardGreenBrush"] = "#57D695"
        };

    public static event EventHandler? Changed;

    public static void Apply(
        string? fontFamilyName,
        double fontSize,
        bool chatAutoScrollEnabled,
        int scalePercent = DefaultScalePercent,
        string? themeName = DefaultThemeName)
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
        ThemeName = NormalizeThemeName(themeName);

        if (Application.Current is { } application)
        {
            ApplyThemeResources(application);
            application.Resources["InterfaceFontFamily"] =
                new FontFamily(FontFamilyName);
            application.Resources["InterfaceFontSize"] = FontSize;
            application.Resources["ChatFontSize"] = FontSize + 1;
            ApplyScaleResource(application);
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void ApplyTheme(string? themeName)
    {
        ThemeName = NormalizeThemeName(themeName);
        if (Application.Current is { } application)
        {
            ApplyThemeResources(application);
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

    public static string NormalizeThemeName(string? themeName) =>
        string.Equals(themeName, DarkThemeName, StringComparison.OrdinalIgnoreCase)
            ? DarkThemeName
            : LightThemeName;

    private static void ApplyScaleResource(Application application)
    {
        var transform = new ScaleTransform(ScaleFactor, ScaleFactor);
        transform.Freeze();
        application.Resources["InterfaceScaleTransform"] = transform;
        foreach (Window window in application.Windows)
        {
            ApplyTextRendering(window);
        }
    }

    public static void ApplyTextRendering(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var formatting = ScalePercent == DefaultScalePercent
            ? TextFormattingMode.Display
            : TextFormattingMode.Ideal;
        TextOptions.SetTextFormattingMode(window, formatting);
        TextOptions.SetTextRenderingMode(window, TextRenderingMode.ClearType);
        if (window.Content is DependencyObject content)
        {
            TextOptions.SetTextFormattingMode(content, formatting);
            TextOptions.SetTextRenderingMode(content, TextRenderingMode.ClearType);
        }
    }

    private static void ApplyThemeResources(Application application)
    {
        var isDark = string.Equals(
            ThemeName,
            DarkThemeName,
            StringComparison.Ordinal);
#pragma warning disable WPF0001
        application.ThemeMode = isDark ? ThemeMode.Dark : ThemeMode.Light;
#pragma warning restore WPF0001

        var palette = isDark ? DarkThemeBrushes : LightThemeBrushes;
        foreach (var (key, value) in palette)
        {
            var color = (Color)ColorConverter.ConvertFromString(value);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            application.Resources[key] = brush;
        }
    }
}
