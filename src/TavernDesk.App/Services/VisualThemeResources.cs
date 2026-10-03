namespace TavernDesk.App.Services;

/// <summary>Derives the visual refresh tokens from the existing theme palette.</summary>
public static class VisualThemeResources
{
    public static IReadOnlyDictionary<string, string> CreatePalette(
        IReadOnlyDictionary<string, string> source, bool dark)
    {
        var result = new Dictionary<string, string>(source, StringComparer.Ordinal);
        void Alias(string target, string origin) => result[target] = result[origin];

        result["MutedTextBrush"] = dark ? "#ADB8C8" : "#626873";
        Alias("SurfaceSunken", "SurfaceAltBrush");
        Alias("SurfaceBase", "SurfaceBrush");
        result["SurfaceRaised"] = dark ? "#2A2F39" : "#FFFFFF";
        result["SurfaceOverlay"] = dark ? "#343A44" : "#FFFFFF";
        Alias("TextPrimary", "TextBrush");
        Alias("TextSecondary", "MutedTextBrush");
        Alias("TextTertiary", "MutedTextBrush");
        result["TextOnAccent"] = dark ? "#101820" : "#FFFFFF";
        result["TextDisabled"] = dark ? "#96A1B2" : "#727782";
        Alias("ControlHover", "ControlHoverBrush");
        Alias("ControlPressed", "ControlPressedBrush");
        Alias("ControlDisabledBackground", "ControlDisabledBrush");
        Alias("ControlDisabledForeground", "TextDisabled");
        Alias("ListItemSelected", "AccentSoftBrush");
        Alias("NavItemSelectedBackground", "ListItemSelected");
        Alias("NavItemIndicatorBrush", "AccentBrush");
        Alias("FocusRing", "FocusRingBrush");
        Alias("ControlTrack", "BorderBrush");
        Alias("AccentFg", "AccentBrush");
        Alias("AccentBg", "AccentSoftBrush");
        Alias("AccentBorder", "AccentBrush");
        Alias("InfoFg", "AccentFg");
        Alias("InfoBg", "AccentBg");
        Alias("InfoBorder", "AccentBorder");
        result["SuccessFg"] = dark ? "#83E4B4" : "#22613E";
        result["SuccessBg"] = dark ? "#19382D" : "#EAF6EE";
        result["WarningFg"] = dark ? "#F2CB8E" : "#86520B";
        result["WarningBg"] = dark ? "#3A3020" : "#FFF4DF";
        result["DangerFg"] = dark ? "#FFA49E" : "#A72B25";
        result["DangerBg"] = dark ? "#3A2023" : "#FFF0EE";
        foreach (var semantic in new[] { "Success", "Warning", "Danger" })
            Alias(semantic + "Border", semantic + "Fg");

        // Keep legacy resource keys working while new styles use the semantic keys.
        Alias("AppicaShellAccentBrush", "AccentBrush");
        Alias("AppicaShellAccentSoftBrush", "AccentSoftBrush");
        Alias("AppicaShellTextBrush", "TextPrimary");
        Alias("AppicaShellMutedBrush", "TextSecondary");
        Alias("AppicaShellSubtleBrush", "TextTertiary");
        return result;
    }
}
