using System.Windows;
using System.Windows.Media;

namespace TavernDesk.App.Services;

public static class InterfaceSettingsRuntime
{
    public const string LightThemeName = "light";
    public const string DarkThemeName = "dark";
    public const string CupertinoThemeName = "cupertino";
    public const string MaterialThemeName = "material";
    public const string DefaultThemeName = LightThemeName;
    public const string DefaultFontFamily = "Microsoft YaHei UI";
    public const double DefaultFontSize = 14;
    public const bool DefaultChatAutoScroll = true;
    public const double MinimumFontSize = 10;
    public const double MaximumFontSize = 30;
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
            ["WindowBackgroundBrush"] = "#F4F5F7",
            ["SurfaceBrush"] = "#FFFFFFFF",
            ["SurfaceSolidBrush"] = "#FFFFFFFF",
            ["SurfaceAltBrush"] = "#F3F4F6",
            ["BorderBrush"] = "#E2E4E9",
            ["TextBrush"] = "#1B1E24",
            ["MutedTextBrush"] = "#6B7280",
            ["AccentBrush"] = "#3B5BDB",
            ["AccentSoftBrush"] = "#EEF1FC",
            ["ControlHoverBrush"] = "#F3F4F6",
            ["ControlPressedBrush"] = "#E8EAEE",
            ["ControlDisabledBrush"] = "#F3F4F6",
            ["FocusRingBrush"] = "#9AAEF0",
            ["ScrollThumbBrush"] = "#C8CCD3",
            ["ScrollThumbHoverBrush"] = "#8D939D",
            ["SuccessBrush"] = "#1A8A5A",
            ["WarningBrush"] = "#B45309",
            ["DangerBrush"] = "#C9372C",
            ["MessagePlusBrush"] = "#7A808B",
            ["InteractionOverlayBrush"] = "#111827",
            ["AppicaSelectedBorderBrush"] = "#C5D2F6",
            ["AppicaPanelBorderBrush"] = "#E2E4E9",
            ["AppicaShellCanvasBrush"] = "#F4F5F7",
            ["AppicaShellSurfaceBrush"] = "#FFFFFFFF",
            ["AppicaShellDividerBrush"] = "#E2E4E9",
            ["AppicaShellTextBrush"] = "#1B1E24",
            ["AppicaShellMutedBrush"] = "#6B7280",
            ["AppicaShellSubtleBrush"] = "#8D939D",
            ["AppicaShellAccentBrush"] = "#3B5BDB",
            ["AppicaShellAccentHoverBrush"] = "#314EC2",
            ["AppicaShellAccentSoftBrush"] = "#EEF1FC",
            ["AppicaShellAccentBorderBrush"] = "#C5D2F6",
            ["AppicaShellHoverBrush"] = "#F3F4F6",
            ["AppicaShellPressedBrush"] = "#E8EAEE",
            ["AppicaShellStatusSurfaceBrush"] = "#F8F9FA",
            ["AppicaShellSuccessBrush"] = "#1A8A5A",
            ["AppicaShellDangerBrush"] = "#C9372C",
            ["AppicaShellDangerSurfaceBrush"] = "#FDF2F2",
            ["AppicaShellDangerHoverBrush"] = "#FDE8E8",
            ["AppicaShellDangerBorderBrush"] = "#F8B4B4",
            ["AppicaShellFocusBrush"] = "#9AAEF0",
            ["AppicaDashboardHeroBrush"] = "#EEF1FC",
            ["AppicaDashboardHeroBorderBrush"] = "#D4DDF8",
            ["AppicaDashboardPurpleSoftBrush"] = "#F3F0FF",
            ["AppicaDashboardPurpleBrush"] = "#6741D9",
            ["AppicaDashboardGreenSoftBrush"] = "#EBFBEE",
            ["AppicaDashboardGreenBrush"] = "#2B8A3E"
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

    private static readonly IReadOnlyDictionary<string, string> CupertinoThemeBrushes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["WindowBackgroundBrush"] = "#F5F5F7",
            ["SurfaceBrush"] = "#FFFFFFFF",
            ["SurfaceSolidBrush"] = "#FFFFFFFF",
            ["SurfaceAltBrush"] = "#EBEBF0",
            ["BorderBrush"] = "#E5E5EA",
            ["TextBrush"] = "#1D1D1F",
            ["MutedTextBrush"] = "#86868B",
            ["AccentBrush"] = "#0071E3",
            ["AccentSoftBrush"] = "#E8F2FF",
            ["ControlHoverBrush"] = "#F0F0F2",
            ["ControlPressedBrush"] = "#E2E2E6",
            ["ControlDisabledBrush"] = "#F5F5F7",
            ["FocusRingBrush"] = "#0071E3",
            ["ScrollThumbBrush"] = "#C7C7CC",
            ["ScrollThumbHoverBrush"] = "#8E8E93",
            ["SuccessBrush"] = "#34C759",
            ["WarningBrush"] = "#FF9500",
            ["DangerBrush"] = "#FF3B30",
            ["MessagePlusBrush"] = "#6E6E73",
            ["InteractionOverlayBrush"] = "#1D1D1F",
            ["AppicaSelectedBorderBrush"] = "#99C8FF",
            ["AppicaPanelBorderBrush"] = "#E5E5EA",
            ["AppicaShellCanvasBrush"] = "#F5F5F7",
            ["AppicaShellSurfaceBrush"] = "#FFFFFFFF",
            ["AppicaShellDividerBrush"] = "#E5E5EA",
            ["AppicaShellTextBrush"] = "#1D1D1F",
            ["AppicaShellMutedBrush"] = "#86868B",
            ["AppicaShellSubtleBrush"] = "#AEAEC2",
            ["AppicaShellAccentBrush"] = "#0071E3",
            ["AppicaShellAccentHoverBrush"] = "#0077ED",
            ["AppicaShellAccentSoftBrush"] = "#E8F2FF",
            ["AppicaShellAccentBorderBrush"] = "#CCE2FF",
            ["AppicaShellHoverBrush"] = "#EDEDF0",
            ["AppicaShellPressedBrush"] = "#E2E2E6",
            ["AppicaShellStatusSurfaceBrush"] = "#F5F5F7",
            ["AppicaShellSuccessBrush"] = "#34C759",
            ["AppicaShellDangerBrush"] = "#FF3B30",
            ["AppicaShellDangerSurfaceBrush"] = "#FFF2F2",
            ["AppicaShellDangerHoverBrush"] = "#FFE5E5",
            ["AppicaShellDangerBorderBrush"] = "#FFD0CE",
            ["AppicaShellFocusBrush"] = "#0071E3",
            ["AppicaDashboardHeroBrush"] = "#F0F6FF",
            ["AppicaDashboardHeroBorderBrush"] = "#D4E5FF",
            ["AppicaDashboardPurpleSoftBrush"] = "#F2F0FF",
            ["AppicaDashboardPurpleBrush"] = "#5856D6",
            ["AppicaDashboardGreenSoftBrush"] = "#EAF8EF",
            ["AppicaDashboardGreenBrush"] = "#28CD41"
        };

    private static readonly IReadOnlyDictionary<string, string> MaterialThemeBrushes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["WindowBackgroundBrush"] = "#F8F6F4",
            ["SurfaceBrush"] = "#FFFFFFFF",
            ["SurfaceSolidBrush"] = "#FFFFFFFF",
            ["SurfaceAltBrush"] = "#F0EAE1",
            ["BorderBrush"] = "#E2DDD5",
            ["TextBrush"] = "#23201E",
            ["MutedTextBrush"] = "#79747E",
            ["AccentBrush"] = "#4E65F4",
            ["AccentSoftBrush"] = "#ECEFFE",
            ["ControlHoverBrush"] = "#F2EDE4",
            ["ControlPressedBrush"] = "#E6DFD4",
            ["ControlDisabledBrush"] = "#F8F6F4",
            ["FocusRingBrush"] = "#4E65F4",
            ["ScrollThumbBrush"] = "#C9C3BA",
            ["ScrollThumbHoverBrush"] = "#99938A",
            ["SuccessBrush"] = "#2E7D32",
            ["WarningBrush"] = "#EF6C00",
            ["DangerBrush"] = "#D32F2F",
            ["MessagePlusBrush"] = "#7A757F",
            ["InteractionOverlayBrush"] = "#23201E",
            ["AppicaSelectedBorderBrush"] = "#B3BEFB",
            ["AppicaPanelBorderBrush"] = "#E2DDD5",
            ["AppicaShellCanvasBrush"] = "#F8F6F4",
            ["AppicaShellSurfaceBrush"] = "#FFFFFFFF",
            ["AppicaShellDividerBrush"] = "#E5E0D7",
            ["AppicaShellTextBrush"] = "#23201E",
            ["AppicaShellMutedBrush"] = "#79747E",
            ["AppicaShellSubtleBrush"] = "#A19C93",
            ["AppicaShellAccentBrush"] = "#4E65F4",
            ["AppicaShellAccentHoverBrush"] = "#3D54E2",
            ["AppicaShellAccentSoftBrush"] = "#ECEFFE",
            ["AppicaShellAccentBorderBrush"] = "#CFD7FD",
            ["AppicaShellHoverBrush"] = "#F2ECE4",
            ["AppicaShellPressedBrush"] = "#E6DFD4",
            ["AppicaShellStatusSurfaceBrush"] = "#F5F1EB",
            ["AppicaShellSuccessBrush"] = "#2E7D32",
            ["AppicaShellDangerBrush"] = "#D32F2F",
            ["AppicaShellDangerSurfaceBrush"] = "#FDEEEE",
            ["AppicaShellDangerHoverBrush"] = "#FBDADA",
            ["AppicaShellDangerBorderBrush"] = "#F8C0C0",
            ["AppicaShellFocusBrush"] = "#4E65F4",
            ["AppicaDashboardHeroBrush"] = "#F0F3FE",
            ["AppicaDashboardHeroBorderBrush"] = "#DCE3FD",
            ["AppicaDashboardPurpleSoftBrush"] = "#F3EDF7",
            ["AppicaDashboardPurpleBrush"] = "#6750A4",
            ["AppicaDashboardGreenSoftBrush"] = "#E8F5E9",
            ["AppicaDashboardGreenBrush"] = "#2E7D32"
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
            application.Resources["FontSizeCaption2"] = Math.Max(9, FontSize - 4);
            application.Resources["FontSizeCaption"] = Math.Max(9, FontSize - 3);
            application.Resources["FontSizeSmall"] = Math.Max(9, FontSize - 2);
            application.Resources["FontSizeBodySmall"] = Math.Max(9, FontSize - 1);
            application.Resources["FontSizeBodyLarge"] = FontSize + 1;
            application.Resources["FontSizeSubtitle"] = FontSize + 2;
            application.Resources["FontSizeSubtitleLarge"] = FontSize + 3;
            application.Resources["FontSizeTitle"] = FontSize + 4;
            application.Resources["DashboardTitleSize"] = FontSize + 14;
            application.Resources["DashboardMetricSize"] = FontSize + 10;
            ApplyScaleResource(application);
        }

        // Publish after all shared resources have changed so subscribers always
        // rebuild against one coherent font/scale/theme state.
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

    public static string NormalizeThemeName(string? themeName)
    {
        if (string.Equals(themeName, DarkThemeName, StringComparison.OrdinalIgnoreCase))
        {
            return DarkThemeName;
        }

        if (string.Equals(themeName, CupertinoThemeName, StringComparison.OrdinalIgnoreCase))
        {
            return CupertinoThemeName;
        }

        if (string.Equals(themeName, MaterialThemeName, StringComparison.OrdinalIgnoreCase))
        {
            return MaterialThemeName;
        }

        return LightThemeName;
    }

    private static void ApplyScaleResource(Application application)
    {
        var transform = new ScaleTransform(ScaleFactor, ScaleFactor);
        // Resource transforms are shared by many visual trees; freezing prevents
        // accidental mutation and lets WPF safely optimize the shared Freezable.
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
        // Always Ideal: Display mode snaps glyphs to whole device pixels, which makes CJK strokes
        // uneven at non-integer DPI (e.g. 125%) and when the interface scale transform is applied.
        const TextFormattingMode formatting = TextFormattingMode.Ideal;
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
        // Cupertino and Material intentionally use the light WPF base styles and
        // supply their complete product palette below. Only the dark preset opts
        // into WPF's dark base resources, avoiding dark values leaking into them.
        application.ThemeMode = isDark ? ThemeMode.Dark : ThemeMode.Light;
#pragma warning restore WPF0001

        var palette = ThemeName switch
        {
            DarkThemeName => DarkThemeBrushes,
            CupertinoThemeName => CupertinoThemeBrushes,
            MaterialThemeName => MaterialThemeBrushes,
            _ => LightThemeBrushes
        };
        foreach (var (key, value) in VisualThemeResources.CreatePalette(palette, isDark))
        {
            var color = (Color)ColorConverter.ConvertFromString(value);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            application.Resources[key] = brush;
        }
    }
}
