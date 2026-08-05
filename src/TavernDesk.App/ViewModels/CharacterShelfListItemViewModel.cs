using TavernDesk.Core.Models;

namespace TavernDesk.App.ViewModels;

public sealed record CharacterShelfListItemViewModel(
    string Id,
    string Name,
    bool IsAllCharacters,
    CharacterShelf? Shelf)
{
    public static CharacterShelfListItemViewModel All { get; } =
        new("__all__", "全部角色", true, null);
}
