namespace TavernDesk.Core.Models;

public enum ThemePreference
{
    System,
    Light,
    Dark
}

public enum CharacterCardScale
{
    Dense,
    Medium,
    Large
}

public sealed record AppUiSettings(
    ThemePreference Theme,
    CharacterCardScale CharacterCardScale,
    bool CompactMode,
    double MainWindowWidth,
    double MainWindowHeight);
