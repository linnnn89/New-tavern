using TavernDesk.App.ViewModels;
using TavernDesk.Infrastructure;

namespace TavernDesk.App.Services;

/// <summary>
/// Keeps the large chat composition root in one place. Each window receives
/// independent selection/UI state while sharing application-level generation
/// coordination and live sessions from InfrastructureServices.
/// </summary>
public sealed class ChatViewModelFactory
{
    private readonly InfrastructureServices _services;
    private readonly IUserInteractionService _interaction;
    private readonly IFileDialogService _fileDialog;

    public ChatViewModelFactory(
        InfrastructureServices services,
        IUserInteractionService interaction,
        IFileDialogService fileDialog)
    {
        _services = services;
        _interaction = interaction;
        _fileDialog = fileDialog;
    }

    public Func<string, Task>? OpenConversationWindow { get; set; }
    public Func<TavernDesk.Core.Models.GlobalPromptKey, Task>? OpenPromptSettings
    {
        get;
        set;
    }

    public ChatViewModel Create()
    {
        var viewModel = new ChatViewModel(
            _services.Conversations,
            _services.Characters,
            _services.MemoryBanks,
            _services.MemoryWorkflow,
            _services.MemoryPrompts,
            _services.GroupChats,
            _services.GroupRelay,
            _services.Retrieval,
            _services.Presets,
            _services.PresetResolver,
            _services.ContextAssembler,
            _services.ContextBudget,
            _services.GenerationCoordinator,
            _services.GenerationSessions,
            _services.ModelAssignments,
            _services.ProviderGateway,
            _services.Settings,
            _services.GlobalPrompts,
            _interaction,
            _services.ChatArchives,
            _fileDialog,
            OpenConversationWindow);
        viewModel.OpenPromptSettings = OpenPromptSettings;
        return viewModel;
    }
}
