using System.Windows;
using TavernDesk.App.Localization;
using TavernDesk.App.Presentation;
using TavernDesk.App.Services;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;
using TavernDesk.Infrastructure;
using TavernDesk.Infrastructure.Context;

namespace TavernDesk.App.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    private readonly IConversationGenerationCoordinator _generationCoordinator;
    private readonly Dictionary<string, ConversationGenerationState>
        _activeGenerationStates = new(StringComparer.Ordinal);
    private object _currentPage;
    private bool _isGenerationActive;
    private bool _isStoppingAll;
    private bool _isRuntimeReceiving;
    private int _runtimeReceivedTokens;
    private string _runtimeReceiveText = string.Empty;
    private string _currentSection = LanguageRuntime.GetString("Runtime.Section.Dashboard");
    private string _runtimeStatusText = LanguageRuntime.GetString("Runtime.Ready");

    public MainWindowViewModel(
        InfrastructureServices services,
        IFileDialogService fileDialog,
        IUserInteractionService interaction,
        ChatViewModel? chat = null)
    {
        _generationCoordinator = services.GenerationCoordinator;
        Dashboard = new DashboardViewModel(
            services.Characters,
            services.Conversations,
            services.Providers,
            OpenRecentConversationAsync);
        // Chat owns application-lifetime generation sessions. Navigation only swaps
        // presentation pages; it must never recreate or dispose this instance.
        var personas = chat?.Personas
                       ?? new PlayerPersonaManagerViewModel(services.Settings, interaction);
        Chat = chat ?? new ChatViewModel(
            services.Conversations,
            services.Characters,
            services.MemoryBanks,
            services.MemoryWorkflow,
            services.MemoryPrompts,
            services.GroupChats,
            services.GroupMemory,
            services.GroupRelay,
            services.Retrieval,
            services.Presets,
            services.PresetResolver,
            services.ContextAssembler,
            new DefaultContextBudgetProvider(),
            services.GenerationCoordinator,
            services.GenerationSessions,
            services.ModelAssignments,
            services.ProviderGateway,
            services.Settings,
            services.GlobalPrompts,
            interaction,
            services.ChatArchives,
            fileDialog,
            personas: personas,
            groupAutoRelayDelay: TimeSpan.FromSeconds(5));
        Characters = new CharactersViewModel(
            services.Characters,
            services.CharacterShelves,
            services.Conversations,
            services.CharacterCards,
            services.Settings,
            fileDialog,
            interaction,
            OpenCharacterChatAsync,
            CreateNewCharacterChatAsync,
            OpenRecentConversationAsync,
            Chat.DeleteConversationAsync);
        Chat.OpenCharacterCard = OpenCharacterCardAsync;
        Settings = new ProviderSettingsViewModel(
            services.Providers,
            services.Models,
            services.ModelAssignments,
            services.Secrets,
            services.ProviderGateway,
            services.ContextBudget,
            interaction,
            services.GlobalPrompts,
            fileDialog,
            services.Settings,
            services.DataLocation,
            personas,
            diagnostics: services.Diagnostics);
        Worldbooks = new WorldbookViewModel(
            services.WorldbookService,
            services.Characters,
            services.CampaignScenarios,
            fileDialog,
            interaction);
        Campaigns = new CampaignsViewModel(
            services.CampaignScenarios,
            services.CampaignScenarioCards,
            services.Campaigns,
            services.CampaignRunner,
            services.Characters,
            services.CampaignCharacterSnapshots,
            services.MemoryBanks,
            services.Providers,
            services.Models,
            services.ModelAssignments,
            services.Settings,
            fileDialog,
            interaction,
            services.WorldbookService,
            services.CampaignMemoryRepository,
            services.CampaignMemory,
            services.CampaignContextPlanner,
            services.CampaignFlowEngine);
        Chat.OpenPromptSettings = OpenPromptSettingsAsync;
        Campaigns.OpenPromptSettings = OpenPromptSettingsAsync;
        services.GenerationCoordinator.StateChanged += OnGenerationStateChanged;

        _currentPage = Dashboard;

        ShowDashboardCommand = new AsyncRelayCommand(ShowDashboardAsync);
        ShowCharactersCommand = new AsyncRelayCommand(ShowCharactersAsync);
        ShowChatCommand = new AsyncRelayCommand(ShowChatAsync);
        ShowCampaignsCommand = new AsyncRelayCommand(ShowCampaignsAsync);
        ShowWorldbooksCommand = new AsyncRelayCommand(ShowWorldbooksAsync);
        ShowSettingsCommand = new AsyncRelayCommand(ShowSettingsAsync);
        StopAllGenerationCommand = new AsyncRelayCommand(
            StopAllGenerationAsync,
            () => IsGenerationActive && !IsStoppingAll);
    }

    public DashboardViewModel Dashboard { get; }
    public CharactersViewModel Characters { get; }
    public ChatViewModel Chat { get; }
    public CampaignsViewModel Campaigns { get; }
    public ProviderSettingsViewModel Settings { get; }
    public WorldbookViewModel Worldbooks { get; }
    public bool IsGenerationActive
    {
        get => _isGenerationActive;
        private set
        {
            if (SetProperty(ref _isGenerationActive, value))
            {
                StopAllGenerationCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsStoppingAll
    {
        get => _isStoppingAll;
        private set
        {
            if (SetProperty(ref _isStoppingAll, value))
            {
                StopAllGenerationCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string RuntimeStatusText
    {
        get => _runtimeStatusText;
        private set => SetProperty(ref _runtimeStatusText, value);
    }

    public bool IsRuntimeReceiving
    {
        get => _isRuntimeReceiving;
        private set => SetProperty(ref _isRuntimeReceiving, value);
    }

    public int RuntimeReceivedTokens
    {
        get => _runtimeReceivedTokens;
        private set => SetProperty(ref _runtimeReceivedTokens, value);
    }

    public string RuntimeReceiveText
    {
        get => _runtimeReceiveText;
        private set => SetProperty(ref _runtimeReceiveText, value);
    }

    public AsyncRelayCommand ShowDashboardCommand { get; }
    public AsyncRelayCommand ShowCharactersCommand { get; }
    public AsyncRelayCommand ShowChatCommand { get; }
    public AsyncRelayCommand ShowCampaignsCommand { get; }
    public AsyncRelayCommand ShowWorldbooksCommand { get; }
    public AsyncRelayCommand ShowSettingsCommand { get; }
    public AsyncRelayCommand StopAllGenerationCommand { get; }

    public object CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    public string CurrentSection
    {
        get => _currentSection;
        private set => SetProperty(ref _currentSection, value);
    }

    public async Task InitializeAsync()
    {
        await Dashboard.LoadAsync();
        await Characters.LoadAsync();
        await Chat.LoadAsync();
        await Campaigns.LoadAsync();
        await Worldbooks.LoadAsync();
        await Settings.LoadAsync();
    }

    private void OnGenerationStateChanged(
        object? sender,
        ConversationGenerationState state)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => ApplyGenerationState(state));
            return;
        }

        ApplyGenerationState(state);
    }

    private void ApplyGenerationState(ConversationGenerationState state)
    {
        int activeCount;
        int receivedTokens;
        lock (_activeGenerationStates)
        {
            if (state.Status is ConversationGenerationStatus.Queued
                or ConversationGenerationStatus.Streaming
                or ConversationGenerationStatus.Stopping)
            {
                _activeGenerationStates[state.ConversationId] = state;
            }
            else
            {
                _activeGenerationStates.Remove(state.ConversationId);
            }

            activeCount = _activeGenerationStates.Count;
            receivedTokens = _activeGenerationStates.Values
                .Sum(item => item.ReceivedTokens);
        }

        IsGenerationActive = activeCount > 0;
        IsRuntimeReceiving = activeCount > 0;
        RuntimeReceivedTokens = receivedTokens;
        RuntimeReceiveText = activeCount == 0
            ? string.Empty
            : LanguageRuntime.Format(
                "Runtime.RequestSummaryFormat",
                activeCount,
                receivedTokens);

        if (IsStoppingAll)
        {
            return;
        }

        RuntimeStatusText = activeCount switch
        {
            0 => LanguageRuntime.GetString("Runtime.Ready"),
            1 => LanguageRuntime.GetString("Runtime.ReceivingOne"),
            _ => LanguageRuntime.Format("Runtime.ReceivingManyFormat", activeCount)
        };
    }

    private async Task StopAllGenerationAsync()
    {
        IsStoppingAll = true;
        RuntimeStatusText = LanguageRuntime.GetString("Runtime.StoppingAll");
        try
        {
            var stopped = await _generationCoordinator.CancelAllAsync();
            RuntimeStatusText = stopped == 0
                ? LanguageRuntime.GetString("Runtime.Ready")
                : LanguageRuntime.Format("Runtime.StoppedFormat", stopped);
        }
        finally
        {
            IsStoppingAll = false;
        }
    }

    private async Task ShowDashboardAsync()
    {
        if (!await ConfirmPageChangeAsync())
        {
            return;
        }

        await Dashboard.LoadAsync();
        CurrentPage = Dashboard;
        CurrentSection = LanguageRuntime.GetString("Runtime.Section.Dashboard");
    }

    private async Task ShowCharactersAsync()
    {
        if (ReferenceEquals(CurrentPage, Characters))
        {
            return;
        }

        if (!await ConfirmPageChangeAsync())
        {
            return;
        }

        await Characters.LoadAsync();
        CurrentPage = Characters;
        CurrentSection = LanguageRuntime.GetString("Runtime.Section.Characters");
    }

    private async Task ShowChatAsync()
    {
        if (!await ConfirmPageChangeAsync())
        {
            return;
        }

        await Chat.LoadAsync();
        CurrentPage = Chat;
        CurrentSection = LanguageRuntime.GetString("Runtime.Section.Chat");
    }

    private async Task ShowCampaignsAsync()
    {
        if (!await ConfirmPageChangeAsync())
        {
            return;
        }

        await Campaigns.LoadAsync();
        CurrentPage = Campaigns;
        CurrentSection = LanguageRuntime.GetString("Runtime.Section.Campaigns");
    }

    private async Task ShowWorldbooksAsync()
    {
        if (ReferenceEquals(CurrentPage, Worldbooks))
        {
            return;
        }

        if (!await ConfirmPageChangeAsync())
        {
            return;
        }

        await Worldbooks.LoadAsync();
        CurrentPage = Worldbooks;
        CurrentSection = LanguageRuntime.GetString("Runtime.Section.Worldbook");
    }

    private async Task ShowSettingsAsync()
    {
        if (ReferenceEquals(CurrentPage, Settings))
        {
            return;
        }

        if (!await ConfirmPageChangeAsync())
        {
            return;
        }

        await Settings.LoadAsync();
        CurrentPage = Settings;
        CurrentSection = LanguageRuntime.GetString("Runtime.Section.Settings");
    }

    public async Task OpenPromptSettingsAsync(GlobalPromptKey key)
    {
        if (!ReferenceEquals(CurrentPage, Settings)
            && !await ConfirmPageChangeAsync())
        {
            return;
        }

        await Settings.LoadAsync();
        Settings.OpenPrompt(key);
        CurrentPage = Settings;
        CurrentSection = LanguageRuntime.GetString("Runtime.Section.Prompts");
    }

    public Task<bool> ConfirmCanCloseAsync() => ConfirmPageChangeAsync();

    private async Task OpenCharacterChatAsync(Character character)
    {
        await Chat.OpenCharacterChatAsync(character);
        CurrentPage = Chat;
        CurrentSection = LanguageRuntime.Format(
            "Runtime.Section.CharacterChatFormat",
            character.Name);
    }

    public async Task OpenCharacterCardAsync(Character character)
    {
        await Characters.OpenCharacterCardAsync(character);
        CurrentPage = Characters;
        CurrentSection = LanguageRuntime.Format(
            "Runtime.Section.CharacterCardFormat",
            character.Name);
    }

    private async Task CreateNewCharacterChatAsync(Character character)
    {
        await Chat.CreateNewCharacterChatAsync(character);
        CurrentPage = Chat;
        CurrentSection = LanguageRuntime.Format(
            "Runtime.Section.NewCharacterChatFormat",
            character.Name);
    }

    private async Task OpenRecentConversationAsync(ConversationSummary conversation)
    {
        await Chat.OpenConversationAsync(conversation.Id);
        CurrentPage = Chat;
        CurrentSection = LanguageRuntime.Format(
            "Runtime.Section.CharacterChatFormat",
            conversation.Title);
    }

    private Task<bool> ConfirmPageChangeAsync()
    {
        if (ReferenceEquals(CurrentPage, Characters))
        {
            return Characters.ConfirmCanLeaveAsync();
        }

        if (ReferenceEquals(CurrentPage, Campaigns))
        {
            return Campaigns.ConfirmCanLeaveAsync();
        }

        return ReferenceEquals(CurrentPage, Settings)
            ? Settings.ConfirmCanLeaveAsync()
            : Task.FromResult(true);
    }
}
