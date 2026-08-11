using TavernDesk.Core.Models;

using TavernDesk.App.Localization;

namespace TavernDesk.App.ViewModels;

public sealed record CharacterShelfListItemViewModel(
    string Id,
    string Name,
    bool IsAllCharacters,
    CharacterShelf? Shelf)
{
    public static CharacterShelfListItemViewModel All { get; } =
        new("__all__", LanguageRuntime.GetString("CharacterShelf.All"), true, null);
}
