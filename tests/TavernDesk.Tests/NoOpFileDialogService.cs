using TavernDesk.App.Services;
using TavernDesk.Core.Models;

namespace TavernDesk.Tests;

internal sealed class NoOpFileDialogService : IFileDialogService
{
    private readonly string? _promptProfileExportPath;

    public NoOpFileDialogService(string? promptProfileExportPath = null)
    {
        _promptProfileExportPath = promptProfileExportPath;
    }

    public string? PickCharacterCard() => null;

    public string? PickCharacterAvatar() => null;

    public string? PickCampaignScenarioCard() => null;

    public string? PickCharacterCardExportPath(Character character) => null;

    public string? PickChatJsonl() => null;

    public string? PickChatJsonlExportPath(string conversationTitle) => null;

    public string? PickPromptProfileExportPath() => _promptProfileExportPath;
}
