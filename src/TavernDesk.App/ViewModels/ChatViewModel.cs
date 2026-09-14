using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Windows;
using TavernDesk.App.Localization;
using TavernDesk.App.Presentation;
using TavernDesk.App.Services;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;
using TavernDesk.Infrastructure.Group;
using TavernDesk.Infrastructure.Context;
using TavernDesk.Infrastructure.Speech;

namespace TavernDesk.App.ViewModels;

public sealed class ChatViewModel : ViewModelBase, IDisposable, IAsyncDisposable
{
    private const string ContinueWithoutUserInstruction =
        "当前用户并未发送回复，但是要求你继续书写聊天发给用户。";

    private readonly IConversationRepository _repository;
    private readonly SpeechPlaybackService? _speech;
    private readonly SpeechSettingsService? _speechSettings;
    private readonly ICharacterRepository _characters;
    private readonly IContextAssembler _contextAssembler;
    private readonly IContextBudgetProvider _contextBudget;
    private readonly IConversationGenerationCoordinator _generationCoordinator;
    private readonly IConversationGenerationSessionStore _generationSessions;
    private readonly IModelAssignmentRepository _modelAssignments;
    private readonly ChatReplyExecutor _chatReplies;
    private readonly IAppSettingsRepository _settings;
    private readonly IGlobalPromptConfiguration _globalPrompts;
    private readonly IUserInteractionService _interaction;
    private readonly IChatArchiveService _chatArchives;
    private readonly IFileDialogService _fileDialog;
    private readonly IGroupChatRepository _groupChats;
    private readonly IGroupMemoryUpdateService _groupMemory;
    private readonly IGroupRelayPlanner _groupRelayPlanner;
    private readonly ConcurrentDictionary<string, string> _conversationStatuses = new();
    private readonly ConcurrentDictionary<string, GroupMemoryScopeMask>
        _invalidGroupMemoryScopes = new();
    private readonly ConcurrentDictionary<string, byte> _unsavedGroupMemoryBodies = new();
    private readonly ConcurrentDictionary<string, byte> _pendingSessionRefreshes = new();
    private readonly TimeSpan _groupAutoRelayDelay;
    private readonly ConversationBrowserViewModel _conversationBrowser;
    private readonly ChatContextPreviewViewModel _contextPreview;
    private readonly PlayerPersonaManagerViewModel _personas;
    private ConversationListItemViewModel? _selectedConversation;
    private CancellationTokenSource? _selectionCancellation;
    private Task _selectionLoadTask = Task.CompletedTask;
    private long _selectionVersion;
    private string? _loadedSelectionId;
    private bool _isSelectionLoading;
    private string _conversationSearchText = string.Empty;
    private string _composerText = string.Empty;
    private bool _isProgrammaticComposerChange;
    private string _status = LanguageRuntime.GetString("Chat.Status.Offline");
    private string _personaName = "USER";
    private string _personaDescription = string.Empty;
    private string _globalPreset = string.Empty;
    private string _personaStatus = LanguageRuntime.GetString("Chat.Persona.Status");
    private string _characterPromptCharacterId = string.Empty;
    private string _characterPromptCharacterName = LanguageRuntime.GetString("Chat.Character.None");
    private string _characterSystemPrompt = string.Empty;
    private string _characterPostHistoryInstructions = string.Empty;
    private string _characterPromptStatus =
        LanguageRuntime.GetString("Chat.CharacterPrompt.Select");
    private string _activeModelText = LanguageRuntime.GetString("Chat.Model.Unassigned");
    private ChatSendMode _sendMode = ChatSendMode.SendAndGenerate;
    private ChatDisplayMode _displayMode = ChatDisplayMode.Bubble;
    private ModelFunctionAssignment? _chatAssignment;
    private ModelFunctionAssignment? _groupChatAssignment;
    private string _groupAutoRelayCountdownText = string.Empty;
    private bool _isGroupAutoRelayCountdownVisible;
    private CancellationTokenSource? _groupAutoRelayCountdownCancellation;
    private bool _disposed;
    private readonly GenerationSessionUpdateQueue _sessionUpdates;

    public ChatViewModel(
        IConversationRepository repository,
        ICharacterRepository characters,
        IMemoryBankService memoryBanks,
        IMemoryWorkflowRepository memoryWorkflow,
        IMemoryPromptComposer memoryPrompts,
        IGroupChatRepository groupChats,
        IGroupMemoryUpdateService groupMemory,
        IGroupRelayPlanner groupRelayPlanner,
        IMessageRetrievalRepository retrieval,
        IPresetRepository presets,
        IPresetResolver presetResolver,
        IContextAssembler contextAssembler,
        IContextBudgetProvider contextBudget,
        IConversationGenerationCoordinator generationCoordinator,
        IConversationGenerationSessionStore generationSessions,
        IModelAssignmentRepository modelAssignments,
        IProviderGateway providerGateway,
        IAppSettingsRepository settings,
        IGlobalPromptConfiguration globalPrompts,
        IUserInteractionService interaction,
        IChatArchiveService chatArchives,
        IFileDialogService fileDialog,
        Func<string, Task>? openConversationWindow = null,
        PlayerPersonaManagerViewModel? personas = null,
        TimeSpan? groupAutoRelayDelay = null,
        ChatReplyExecutor? chatReplies = null,
        SpeechPlaybackService? speech = null,
        SpeechSettingsService? speechSettings = null)
    {
        _conversationBrowser = new ConversationBrowserViewModel(repository, characters, generationCoordinator, openConversationWindow, DeleteConversationAsync);
        _repository = repository;
        _speech = speech;
        _speechSettings = speechSettings;
        if (_speech is not null) _speech.Changed += OnSpeechChanged;
        _characters = characters;
        _groupChats = groupChats;
        _groupMemory = groupMemory;
        _groupRelayPlanner = groupRelayPlanner;
        _contextAssembler = contextAssembler;
        _contextBudget = contextBudget;
        _generationCoordinator = generationCoordinator;
        _generationSessions = generationSessions;
        _modelAssignments = modelAssignments;
        _chatReplies = chatReplies ?? new ChatReplyExecutor(
            repository, providerGateway, generationCoordinator, generationSessions);
        _settings = settings;
        _globalPrompts = globalPrompts;
        _interaction = interaction;
        _chatArchives = chatArchives;
        _fileDialog = fileDialog;
        _groupAutoRelayDelay = groupAutoRelayDelay ?? TimeSpan.Zero;
        _personas = personas ?? new PlayerPersonaManagerViewModel(settings, interaction);
        _personas.PropertyChanged += OnPersonaManagerPropertyChanged;
        Memory = new MemoryWorkflowViewModel(
            memoryBanks,
            memoryWorkflow,
            memoryPrompts,
            repository,
            characters,
            modelAssignments,
            providerGateway,
            generationCoordinator,
            globalPrompts);
        Group = new GroupChatViewModel(
            groupChats,
            groupRelayPlanner,
            characters,
            interaction,
            OpenGroupConversationAsync,
            StartGroupContinueAsync,
            GenerateGroupMergeAsync,
            UpdateGroupMemoryAsync,
            character => OpenCharacterCard?.Invoke(character) ?? Task.CompletedTask,
            () => IsCurrentConversationBusy);
        Retrieval = new RetrievalViewModel(retrieval, ScheduleContextRefresh);
        Presets = new PresetViewModel(
            presets,
            presetResolver,
            interaction,
            ScheduleContextRefresh);
        Memory.BodyChanged += OnMemoryBodyChanged;
        Memory.BodySaved += OnMemoryBodySaved;
        _contextPreview = new ChatContextPreviewViewModel(
            contextAssembler, BudgetFor(ConversationMode.SingleCharacter));

        SelectConversationCommand = new RelayCommand(parameter =>
        {
            if (parameter is ConversationListItemViewModel conversation)
            {
                SelectConversation(conversation);
            }
        });
        SendLocalCommand = new RelayCommand(
            StartSend,
            () => CanSendLocal());
        StopGenerationCommand = new RelayCommand(
            StopCurrentGeneration,
            () => IsCurrentConversationGenerating);
        StopGroupAutoRelayCommand = new RelayCommand(
            StopGroupAutoRelay,
            () => IsGroupAutoRelayCountdownVisible);
        SavePersonaCommand = new AsyncRelayCommand(SavePersonaAsync);
        CancelPersonaCommand = new RelayCommand(CancelPersonaEdits);
        EditCharacterSystemPromptCommand = new AsyncRelayCommand(
            EditCharacterSystemPromptAsync,
            CanEditCharacterPrompt);
        EditCharacterPostHistoryCommand = new AsyncRelayCommand(
            EditCharacterPostHistoryAsync,
            CanEditCharacterPrompt);
        OpenGlobalPromptCommand = new AsyncRelayCommand(OpenGlobalPromptAsync);
        ImportChatArchiveCommand = new AsyncRelayCommand(ImportChatArchiveAsync);
        ExportChatArchiveCommand = new AsyncRelayCommand(
            ExportChatArchiveAsync,
            () => SelectedConversation is not null);
        _contextPreview.PropertyChanged += OnContextPreviewPropertyChanged;
        _contextPreview.PreviewApplied += OnContextPreviewApplied;
        _generationCoordinator.StateChanged += OnGenerationStateChanged;
        _sessionUpdates = new GenerationSessionUpdateQueue(generationSessions, Application.Current?.Dispatcher, ApplyGenerationSession);
    }

    public ObservableCollection<CharacterConversationGroupViewModel> ConversationGroups => _conversationBrowser.Groups;
    public ObservableCollection<ChatMessageItemViewModel> Messages { get; } = [];
    public ObservableCollection<ContextSegment> ContextSegments => _contextPreview.ContextSegments;
    public MemoryWorkflowViewModel Memory { get; }
    public GroupChatViewModel Group { get; }
    public RetrievalViewModel Retrieval { get; }
    public PresetViewModel Presets { get; }
    public PlayerPersonaManagerViewModel Personas => _personas;
    public RelayCommand SelectConversationCommand { get; }
    public RelayCommand SendLocalCommand { get; }
    public RelayCommand StopGenerationCommand { get; }
    public RelayCommand StopGroupAutoRelayCommand { get; }
    public AsyncRelayCommand SavePersonaCommand { get; }
    public RelayCommand CancelPersonaCommand { get; }
    public AsyncRelayCommand EditCharacterSystemPromptCommand { get; }
    public AsyncRelayCommand EditCharacterPostHistoryCommand { get; }
    public AsyncRelayCommand OpenGlobalPromptCommand { get; }
    public AsyncRelayCommand ImportChatArchiveCommand { get; }
    public AsyncRelayCommand ExportChatArchiveCommand { get; }
    public Func<Character, Task>? OpenCharacterCard { get; set; }
    public Func<GlobalPromptKey, Task>? OpenPromptSettings { get; set; }

    public string GroupAutoRelayCountdownText => _groupAutoRelayCountdownText;
    public bool IsGroupAutoRelayCountdownVisible =>
        _isGroupAutoRelayCountdownVisible;
    public IReadOnlyList<ChatSendModeOption> SendModes { get; } =
    [
        new(ChatSendMode.SendAndGenerate, LanguageRuntime.GetString("Chat.SendMode.SendAndGenerate")),
        new(ChatSendMode.SaveOnly, LanguageRuntime.GetString("Chat.SendMode.SaveOnly"))
    ];
    public IReadOnlyList<ChatDisplayModeOption> DisplayModes { get; } =
    [
        new(ChatDisplayMode.Bubble, LanguageRuntime.GetString("Chat.Display.Bubble")),
        new(ChatDisplayMode.Novel, LanguageRuntime.GetString("Chat.Display.Novel"))
    ];

    public string ConversationSearchText
    {
        get => _conversationSearchText;
        set
        {
            if (SetProperty(ref _conversationSearchText, value))
            {
                ApplyConversationFilter();
            }
        }
    }

    public ConversationListItemViewModel? SelectedConversation
    {
        get => _selectedConversation;
        private set
        {
            if (SetProperty(ref _selectedConversation, value))
            {
                ApplyCharacterPrompts(null);
                Status = value is not null
                         && _conversationStatuses.TryGetValue(value.Id, out var status)
                    ? status
                    : value is null
                        ? LanguageRuntime.GetString("Chat.SelectConversation")
                        : LanguageRuntime.GetString("Chat.ConversationLoaded");
                OnPropertyChanged(nameof(IsCurrentConversationGenerating));
                OnPropertyChanged(nameof(IsCurrentConversationBusy));
                OnPropertyChanged(nameof(CanEditGroupMembers));
                Group.RefreshGenerationState();
                OnPropertyChanged(nameof(IsModelThinking));
                OnPropertyChanged(nameof(LastGenerationUsageText));
                OnPropertyChanged(nameof(IsSingleCharacterConversation));
                OnPropertyChanged(nameof(SelectedConversationAvatarPath));
                StopGenerationCommand.RaiseCanExecuteChanged();
                ExportChatArchiveCommand.RaiseCanExecuteChanged();
                EditCharacterSystemPromptCommand.RaiseCanExecuteChanged();
                EditCharacterPostHistoryCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsSingleCharacterConversation =>
        SelectedConversation?.Mode == ConversationMode.SingleCharacter;

    public string SelectedConversationAvatarPath
    {
        get
        {
            var conversationId = SelectedConversation?.Id;
            if (conversationId is null)
            {
                return string.Empty;
            }

            return _conversationBrowser.AllGroups.FirstOrDefault(group =>
                       group.FindConversation(conversationId) is not null)?.AvatarPath
                   ?? string.Empty;
        }
    }

    public string CharacterPromptCharacterName
    {
        get => _characterPromptCharacterName;
        private set => SetProperty(ref _characterPromptCharacterName, value);
    }

    public string CharacterSystemPrompt
    {
        get => _characterSystemPrompt;
        private set => SetProperty(ref _characterSystemPrompt, value);
    }

    public string CharacterPostHistoryInstructions
    {
        get => _characterPostHistoryInstructions;
        private set => SetProperty(ref _characterPostHistoryInstructions, value);
    }

    public string CharacterPromptStatus
    {
        get => _characterPromptStatus;
        private set => SetProperty(ref _characterPromptStatus, value);
    }

    public string ComposerText
    {
        get => _composerText;
        set
        {
            if (!SetProperty(ref _composerText, value))
            {
                return;
            }

            if (!_isProgrammaticComposerChange)
            {
                _contextPreview.ReleaseActualBudget();
                if (SelectedConversation?.Mode == ConversationMode.Group
                    && string.Equals(
                        Group.ConversationId,
                        SelectedConversation.Id,
                        StringComparison.Ordinal))
                {
                    // Typing is the user's explicit interruption of automatic
                    // relay.  The setting remains local until the user saves
                    // group settings, so a transient pause is reversible.
                    Group.SuppressAutoContinue();
                    _groupAutoRelayCountdownCancellation?.Cancel();
                    ClearGroupAutoRelayCountdown();
                }
                ScheduleContextRefresh();
            }
            SendLocalCommand.RaiseCanExecuteChanged();
        }
    }

    public string EstimatedTokenText => _contextPreview.EstimatedTokenText;
    public int EstimatedInputTokens => _contextPreview.EstimatedInputTokens;
    public string EstimatedTokenHeadline => _contextPreview.EstimatedTokenHeadline;
    public double EstimatedTokenUsagePercent => _contextPreview.EstimatedTokenUsagePercent;
    public string EstimatedTokenUsageLevel => _contextPreview.EstimatedTokenUsageLevel;
    public bool IsEstimatedOverLimit => _contextPreview.IsEstimatedOverLimit;
    public GroupContextBudgetResult? ContextBudgetResult => _contextPreview.ContextBudgetResult;

    public bool IsModelThinking =>
        SelectedConversation is not null
        && _generationSessions.Get(SelectedConversation.Id).IsThinking;

    public string LastGenerationUsageText
    {
        get
        {
            if (SelectedConversation is null)
            {
                return LanguageRuntime.GetString("Chat.Token.ActualSelect");
            }

            var telemetry = _generationSessions.Get(SelectedConversation.Id);
            if (telemetry.OperationId is null)
            {
                return LanguageRuntime.GetString("Chat.Token.NoActual");
            }

            if (telemetry.Usage is null)
            {
                return telemetry.IsBusy
                    ? LanguageRuntime.GetString("Chat.Token.GenerationBusy")
                    : LanguageRuntime.GetString("Chat.Token.NotReturned");
            }

            var usage = telemetry.Usage;
            var reasoning = usage.ReasoningTokens is > 0
                ? LanguageRuntime.Format("Chat.Token.ReasoningFormat", usage.ReasoningTokens)
                : string.Empty;
            var cache = usage.CachedPromptTokens is { } cached
                ? LanguageRuntime.Format("Chat.Token.CacheHitFormat", cached)
                  + (usage.UncachedPromptTokens is { } uncached
                      ? LanguageRuntime.Format("Chat.Token.CacheMissFormat", uncached)
                      : string.Empty)
                : string.Empty;
            return LanguageRuntime.Format(
                "Chat.Token.ActualFormat",
                usage.PromptTokens,
                usage.CompletionTokens,
                usage.TotalTokens,
                reasoning,
                cache,
                FinishReasonLabel(telemetry.FinishReason));
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string PersonaName
    {
        get => _personaName;
        set
        {
            if (SetProperty(ref _personaName, value))
            {
                Memory.SetUserIdentity(value);
                RefreshPersonaPresentation();
                ScheduleContextRefresh();
            }
        }
    }

    public string PersonaDescription
    {
        get => _personaDescription;
        set
        {
            if (SetProperty(ref _personaDescription, value))
            {
                ScheduleContextRefresh();
            }
        }
    }

    public string GlobalPreset
    {
        get => _globalPreset;
        set
        {
            if (SetProperty(ref _globalPreset, value))
            {
                ScheduleContextRefresh();
            }
        }
    }

    public string PersonaStatus
    {
        get => _personaStatus;
        private set => SetProperty(ref _personaStatus, value);
    }

    private void OnPersonaManagerPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerPersonaManagerViewModel.SelectedProfile)
            or nameof(PlayerPersonaManagerViewModel.ActiveName)
            or nameof(PlayerPersonaManagerViewModel.ActiveDescription)
            or nameof(PlayerPersonaManagerViewModel.Status))
        {
            ApplyActivePersona();
            PersonaStatus = _personas.Status;
        }
    }

    private void ApplyActivePersona()
    {
        PersonaName = _personas.ActiveName;
        PersonaDescription = _personas.ActiveDescription;
    }

    public string ActiveModelText
    {
        get => _activeModelText;
        private set => SetProperty(ref _activeModelText, value);
    }

    public string ApiRequestPreview => _contextPreview.ApiRequestPreview;

    public ChatSendMode SendMode
    {
        get => _sendMode;
        set
        {
            if (SetProperty(ref _sendMode, value))
            {
                SendLocalCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public ChatDisplayMode DisplayMode
    {
        get => _displayMode;
        set
        {
            if (!SetProperty(ref _displayMode, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsNovelMode));
            _ = SaveDisplayModeAsync(value);
        }
    }

    public bool IsNovelMode => DisplayMode == ChatDisplayMode.Novel;

    public bool IsCurrentConversationGenerating =>
        SelectedConversation is not null
        && (_generationSessions.Get(SelectedConversation.Id).IsBusy
            || _generationCoordinator.GetState(SelectedConversation.Id).Status
                is ConversationGenerationStatus.Queued
                or ConversationGenerationStatus.Streaming
                or ConversationGenerationStatus.Stopping);

    public bool IsCurrentConversationBusy =>
        SelectedConversation is not null
        && IsConversationBusy(SelectedConversation.Id);

    public bool CanEditGroupMembers => !IsCurrentConversationBusy;

    public bool IsConversationBusy(string conversationId) =>
        _generationSessions.Get(conversationId).IsBusy
        || _pendingSessionRefreshes.ContainsKey(conversationId)
        || _generationCoordinator.GetState(conversationId).Status
            is ConversationGenerationStatus.Queued
            or ConversationGenerationStatus.Streaming
            or ConversationGenerationStatus.Stopping;

    public async Task LoadAsync()
    {
        await LoadPersonaAsync();
        await RefreshAssignmentsAsync();
        await ReloadGroupsAsync(SelectedConversation?.Id);
    }

    public async Task OpenCharacterChatAsync(Character character)
    {
        var latest = await _repository.GetLatestForCharacterAsync(character.Id);
        if (latest is null)
        {
            var created = await CreateCharacterConversationAsync(character);
            await ReloadGroupsAsync(created.Id);
            return;
        }

        await ReloadGroupsAsync(latest.Id);
    }

    public async Task CreateNewCharacterChatAsync(Character character)
    {
        var created = await CreateCharacterConversationAsync(character);
        await ReloadGroupsAsync(created.Id);
    }

    public async Task OpenConversationAsync(string conversationId)
    {
        await ReloadGroupsAsync(conversationId);
    }

    private async Task OpenGroupConversationAsync(string conversationId)
    {
        await OpenConversationAsync(conversationId);
    }

    private async Task ImportChatArchiveAsync()
    {
        var path = _fileDialog.PickChatJsonl();
        if (path is null)
        {
            return;
        }

        try
        {
            Status = LanguageRuntime.GetString("Chat.Import.Starting");
            var result = await _chatArchives.ImportAsync(path);
            await ReloadGroupsAsync(result.Conversation.Id);
            var warningText = result.Warnings.Count == 0
                ? string.Empty
                : LanguageRuntime.Format("Chat.Import.WarningFormat", result.Warnings.Count);
            Status = LanguageRuntime.Format(
                "Chat.Import.DoneFormat",
                result.MessageCount,
                result.CandidateCount,
                result.CharacterName,
                warningText);
        }
        catch (Exception exception)
        {
            Status = LanguageRuntime.Format("Chat.Import.FailedFormat", LanguageRuntime.ErrorMessage(exception));
        }
    }

    private async Task ExportChatArchiveAsync()
    {
        var selected = SelectedConversation;
        if (selected is null)
        {
            return;
        }

        var path = _fileDialog.PickChatJsonlExportPath(selected.Title);
        if (path is null)
        {
            return;
        }

        try
        {
            Status = LanguageRuntime.GetString("Chat.Export.Starting");
            var result = await _chatArchives.ExportAsync(selected.Id, path);
            Status = LanguageRuntime.Format(
                "Chat.Export.DoneFormat",
                result.MessageCount,
                result.CandidateCount,
                result.Warnings.Count == 0
                    ? LanguageRuntime.GetString("Chat.Export.Period")
                    : LanguageRuntime.Format("Chat.Export.WarningFormat", result.Warnings.Count));
        }
        catch (Exception exception)
        {
            Status = LanguageRuntime.Format("Chat.Export.FailedFormat", LanguageRuntime.ErrorMessage(exception));
        }
    }

    private Task DeleteConversationAsync(ConversationListItemViewModel conversation) =>
        DeleteConversationAsync(conversation.Id, conversation.Title);

    public async Task DeleteConversationAsync(
        string conversationId,
        string conversationTitle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentNullException.ThrowIfNull(conversationTitle);
        var selectedConversationId = SelectedConversation?.Id;
        var isSelected = string.Equals(
            selectedConversationId,
            conversationId,
            StringComparison.Ordinal);
        if (IsConversationBusy(conversationId)
            || (isSelected && Memory.IsGenerating))
        {
            SetStatusForConversation(
                conversationId,
                LanguageRuntime.GetString("Chat.Delete.Busy"));
            return;
        }

        if (!_interaction.ConfirmConversationDeletion(conversationTitle))
        {
            return;
        }

        _speech?.StopConversation(conversationId);
        if (isSelected)
        {
            // Stop selection/context reloads before deleting the database row;
            // the UI cache is cleared, while Memory.Clear only clears the
            // presentation state and never deletes the character's memory bank.
            ClearSelection();
        }

        try
        {
            await _repository.DeleteConversationAsync(conversationId);
            _conversationStatuses.TryRemove(conversationId, out _);
            _pendingSessionRefreshes.TryRemove(conversationId, out _);
            _generationSessions.Forget(conversationId);
            await ReloadGroupsAsync(isSelected
                ? null
                : selectedConversationId);
            Status = LanguageRuntime.Format("Chat.Delete.DoneFormat", conversationTitle);
        }
        catch (Exception exception)
        {
            // If a delete fails after the selected view was cleared, reload the
            // row so the user does not lose the current chat presentation.
            await ReloadGroupsAsync(selectedConversationId);
            Status = LanguageRuntime.Format("Chat.Delete.FailedFormat", LanguageRuntime.ErrorMessage(exception));
        }
    }

    private async Task<Conversation> CreateCharacterConversationAsync(Character character)
    {
        var conversation = new Conversation
        {
            CharacterId = character.Id,
            Title = character.Name,
            Mode = ConversationMode.SingleCharacter
        };
        await _repository.UpsertAsync(conversation);

        if (!string.IsNullOrWhiteSpace(character.FirstMessage))
        {
            await _repository.AddMessageAsync(new ChatMessage
            {
                ConversationId = conversation.Id,
                SenderKind = MessageSenderKind.Character,
                SenderId = character.Id,
                Content = character.FirstMessage
            });
        }

        return conversation;
    }

    private async Task ReloadGroupsAsync(string? preferredConversationId)
    {
        await _conversationBrowser.ReloadAsync(() => preferredConversationId, ApplyConversationFilter);
        await _selectionLoadTask;
    }

    private void ApplyConversationFilter(string? preferredConversationId = null)
    {
        var selectedId = preferredConversationId ?? SelectedConversation?.Id;
        _conversationBrowser.ApplyFilter(ConversationSearchText);

        var next = FindConversation(selectedId);
        if (next is not null)
        {
            SelectConversation(next);
        }
        else if (selectedId is not null)
        {
            ClearSelection();
        }
    }

    private ConversationListItemViewModel? FindConversation(string? conversationId) =>
        _conversationBrowser.FindConversation(conversationId);

    private void SelectConversation(ConversationListItemViewModel conversation)
    {
        if (ReferenceEquals(SelectedConversation, conversation))
        {
            return;
        }

        if (SelectedConversation is not null)
        {
            StopVisibleSpeech();
            SelectedConversation.IsSelected = false;
        }

        conversation.IsSelected = true;
        SelectedConversation = conversation;
        ApplyActiveAssignmentBudget(conversation.Mode);
        StartSelectionLoad(conversation);
        SendLocalCommand.RaiseCanExecuteChanged();
    }

    private void ClearSelection()
    {
        StopVisibleSpeech();
        _selectionCancellation?.Cancel();
        _selectionCancellation?.Dispose();
        _selectionCancellation = null;

        if (SelectedConversation is not null)
        {
            SelectedConversation.IsSelected = false;
        }

        SelectedConversation = null;
        _loadedSelectionId = null;
        _isSelectionLoading = false;
        Messages.Clear();
        _contextPreview.BeginSelection(SelectedConversation?.Id, CurrentUiBudget);
        ForgetUnsavedGroupMemoryBody();
        Memory.Clear();
        Group.Clear();
        Retrieval.Clear();
        Presets.Clear();
        ApplyCharacterPrompts(null);

        SendLocalCommand.RaiseCanExecuteChanged();
    }

    private void StartSelectionLoad(ConversationListItemViewModel conversation)
    {
        // Cancellation is advisory for SQLite/WPF continuations; the monotonic
        // version also prevents a late load from replacing a newer selection.
        _selectionCancellation?.Cancel();
        _selectionCancellation?.Dispose();
        _selectionCancellation = new CancellationTokenSource();
        var version = ++_selectionVersion;
        _loadedSelectionId = null;
        _isSelectionLoading = true;
        Messages.Clear();
        _contextPreview.BeginSelection(conversation.Id, CurrentUiBudget);
        ForgetUnsavedGroupMemoryBody();
        Memory.Clear();
        Group.Clear();
        Retrieval.Clear();
        Presets.Clear();
        ApplyCharacterPrompts(null);
        SendLocalCommand.RaiseCanExecuteChanged();
        RefreshContinueGenerationCommands();
        _selectionLoadTask = LoadSelectionAsync(
            conversation,
            version,
            _selectionCancellation.Token);
    }

    private async Task LoadSelectionAsync(
        ConversationListItemViewModel conversation,
        long version,
        CancellationToken cancellationToken)
    {
        try
        {
            // Microsoft.Data.Sqlite's async methods still execute synchronously.
            // Keep the complete history read off the dispatcher, then apply only the current selection.
            var snapshot = await Task.Run(async () =>
            {
                var messages = await _repository.ListMessagesAsync(conversation.Id, cancellationToken);
                var selected = await _repository.GetAsync(conversation.Id, cancellationToken);
                var candidates = await _repository.ListCandidatesForConversationAsync(conversation.Id, cancellationToken);
                return (messages, selected, candidates);
            }, cancellationToken);
            var loadedConversation = snapshot.selected
                ?? throw new InvalidOperationException(
                    LanguageRuntime.GetString("Chat.ConversationMissing"));

            if (cancellationToken.IsCancellationRequested
                || version != _selectionVersion
                || SelectedConversation?.Id != conversation.Id)
            {
                return;
            }

            var loadedMessages = snapshot.messages;
            var candidatesByMessage = snapshot.candidates;
            if (cancellationToken.IsCancellationRequested
                || version != _selectionVersion
                || SelectedConversation?.Id != conversation.Id)
            {
                return;
            }

            Messages.Clear();
            for (var index = 0; index < loadedMessages.Count; index++)
            {
                if (index > 0 && index % 50 == 0 && System.Windows.Application.Current?.Dispatcher.CheckAccess() == true)
                {
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                    if (cancellationToken.IsCancellationRequested || version != _selectionVersion
                        || SelectedConversation?.Id != conversation.Id) return;
                }
                var message = loadedMessages[index];
                if (loadedConversation.Mode == ConversationMode.Group
                    && message.SenderKind == MessageSenderKind.Character
                    && _conversationBrowser.Characters.TryGetValue(
                        message.SenderId,
                        out var historyCharacter))
                {
                    message.Content = GroupRelayResponseNormalizer
                        .StripSyntheticHistoryPrefix(
                            message.Content,
                            historyCharacter.Name);
                }

                Messages.Add(CreateMessageItem(
                    message,
                    candidatesByMessage.GetValueOrDefault(message.Id) ?? []));
            }
            RefreshContinueGenerationCommands();
            ApplyLiveSession(_generationSessions.Get(conversation.Id));

            var ownerId = loadedConversation.Mode == ConversationMode.Group
                ? MemoryOwnerIds.ForGroup(loadedConversation.Id)
                : loadedConversation.CharacterId
                  ?? throw new InvalidOperationException(
                      LanguageRuntime.GetString("Chat.CharacterReferenceMissing"));
            var ownerLabel = loadedConversation.Mode == ConversationMode.Group
                ? LanguageRuntime.Format("Chat.Memory.GroupFormat", loadedConversation.Title)
                : LanguageRuntime.Format(
                    "Chat.Memory.CharacterFormat",
                    _conversationBrowser.Characters.GetValueOrDefault(ownerId)?.Name
                    ?? loadedConversation.Title);
            Character? promptCharacter = null;
            if (loadedConversation.Mode == ConversationMode.SingleCharacter)
            {
                promptCharacter = await _characters.GetAsync(
                    ownerId,
                    cancellationToken)
                    ?? throw new InvalidOperationException(
                        LanguageRuntime.GetString("Chat.CharacterMissing"));
                if (cancellationToken.IsCancellationRequested
                    || version != _selectionVersion
                    || SelectedConversation?.Id != conversation.Id)
                {
                    return;
                }
            }

            ApplyCharacterPrompts(promptCharacter);
            await Task.WhenAll(
                Memory.LoadAsync(
                    ownerId,
                    loadedConversation.Id,
                    ownerLabel,
                    cancellationToken,
                    PersonaName),
                Group.LoadAsync(loadedConversation, cancellationToken),
                Retrieval.LoadAsync(loadedConversation, cancellationToken),
                Presets.LoadAsync(loadedConversation, cancellationToken));
            if (cancellationToken.IsCancellationRequested
                || version != _selectionVersion
                || SelectedConversation?.Id != conversation.Id
                || !string.Equals(
                    Memory.ConversationId,
                    conversation.Id,
                    StringComparison.Ordinal)
                || (loadedConversation.Mode == ConversationMode.Group
                    && !string.Equals(
                        Group.ConversationId,
                        conversation.Id,
                        StringComparison.Ordinal)))
            {
                return;
            }

            _loadedSelectionId = conversation.Id;
            _isSelectionLoading = false;
            SendLocalCommand.RaiseCanExecuteChanged();
            RefreshContinueGenerationCommands();
            await RefreshContextEstimateAsync(
                immediate: true,
                cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (version == _selectionVersion)
            {
                _loadedSelectionId = null;
                _isSelectionLoading = false;
                SendLocalCommand.RaiseCanExecuteChanged();
                RefreshContinueGenerationCommands();
                Status = LanguageRuntime.Format("Chat.ReadFailedFormat", LanguageRuntime.ErrorMessage(exception));
            }
        }
    }

    private bool IsSelectionReady(string? conversationId = null)
    {
        var selected = SelectedConversation;
        return selected is not null
               && !_isSelectionLoading
               && string.Equals(
                   _loadedSelectionId,
                   conversationId ?? selected.Id,
                   StringComparison.Ordinal)
               && string.Equals(
                   selected.Id,
                   conversationId ?? selected.Id,
                   StringComparison.Ordinal)
               && string.Equals(
                   Memory.ConversationId,
                   selected.Id,
                   StringComparison.Ordinal)
               && (selected.Mode != ConversationMode.Group
                   || string.Equals(
                       Group.ConversationId,
                       selected.Id,
                       StringComparison.Ordinal));
    }

    private bool CanSendLocal()
    {
        var selected = SelectedConversation;
        return selected is not null
               && IsSelectionReady(selected.Id)
               && !string.IsNullOrWhiteSpace(ComposerText)
               && !IsEstimatedOverLimit
               && !IsCurrentConversationBusy
               && (SendMode == ChatSendMode.SaveOnly
                   || AssignmentFor(selected.Mode) is not null);
    }

    private void StartSend()
    {
        StopVisibleSpeech();
        var selected = SelectedConversation;
        if (selected is null
            || !IsSelectionReady(selected.Id)
            || !_generationSessions.TryBegin(
                selected.Id,
                out var operationId))
        {
            return;
        }

        var budget = BudgetFor(selected.Mode);
        var snapshot = new SendSnapshot(
            selected.Id,
            selected.Mode,
            selected.CharacterId,
            ComposerText.Trim(),
            SendMode,
            AssignmentFor(selected.Mode),
            CreateContextSnapshot(budget),
            operationId);
        RaiseCurrentConversationBusyChanged(selected.Id);
        SendLocalCommand.RaiseCanExecuteChanged();
        _ = SendAsync(snapshot);
    }

    private async Task StartGroupContinueAsync(string? manualSpeakerId)
    {
        var selected = SelectedConversation;
        if (selected?.Mode != ConversationMode.Group
            || !IsSelectionReady(selected.Id))
        {
            Status = LanguageRuntime.GetString("Chat.Group.NotCurrent");
            return;
        }

        if (!_generationSessions.TryBegin(
                selected.Id,
                out var operationId))
        {
            Status = LanguageRuntime.GetString("Chat.Group.AlreadyGenerating");
            return;
        }

        RaiseCurrentConversationBusyChanged(selected.Id);
        var operationCancellation = _generationSessions.GetCancellationToken(
            selected.Id,
            operationId);
        var groupMessagePersisted = false;
        try
        {
            var assignment = _groupChatAssignment;
            if (assignment is null)
            {
                Status = LanguageRuntime.GetString("Chat.Group.ModelUnassigned");
                return;
            }

            var snapshot = new SendSnapshot(
                selected.Id,
                ConversationMode.Group,
                CharacterId: null,
                Input: string.Empty,
                ChatSendMode.SendAndGenerate,
                assignment,
                CreateContextSnapshot(
                    BudgetFor(ConversationMode.Group),
                    manualSpeakerId),
                operationId);
            var messages = await _repository.ListMessagesAsync(
                selected.Id,
                operationCancellation);
            var manualSpeakerIsMember = manualSpeakerId is { Length: > 0 }
                && snapshot.Context.Group?.Members.Any(member =>
                    member.CharacterId == manualSpeakerId) == true;
            var decision = manualSpeakerIsMember
                ? new GroupRelayDecision(
                    manualSpeakerId,
                    false,
                    "group-force-selected")
                : DecideGroupNext(snapshot.Context, messages);
            if (decision.NextSpeakerId is null)
            {
                await SaveGroupStateAsync(
                    selected.Id,
                    messages.LastOrDefault(message =>
                        message.SenderKind == MessageSenderKind.Character)?.SenderId
                    ?? string.Empty,
                    string.Empty,
                    0,
                    isPaused: true,
                    decision.Reason,
                    operationCancellation);
                SetStatusForConversation(
                    selected.Id,
                    LanguageRuntime.GroupRelayReason(decision.Reason));
                return;
            }

            var context = await AssembleContextAsync(
                selected.Id,
                userInput: string.Empty,
                historyBeforeSequenceNo: null,
                snapshot: snapshot.Context with
                {
                    SpeakerCharacterId = decision.NextSpeakerId
                },
                cancellationToken: operationCancellation);
            PublishActualContextBudget(selected.Id, context);
            if (!CanSendContext(context))
            {
                SetStatusForConversation(
                    selected.Id,
                    LanguageRuntime.GetString("Chat.Group.ContextOverLimit"));
                return;
            }

            var assistant = await GenerateReplyAsync(
                snapshot,
                assignment,
                context,
                decision.NextSpeakerId);
            if (assistant is null)
            {
                if (!IsGenerationInterrupted(selected.Id))
                {
                    await PauseGroupRelayForInvalidReplyAsync(
                        selected.Id,
                        decision.NextSpeakerId,
                        decision.NextSpeakerId,
                        0,
                        operationCancellation);
                }

                return;
            }

            groupMessagePersisted = true;

            await ReloadGroupsPreservingSelectionAsync();
            if (_generationCoordinator.GetState(selected.Id).Status
                != ConversationGenerationStatus.Interrupted)
            {
                await ContinueGroupRelayAsync(snapshot, assistant);
            }

        }
        catch (OperationCanceledException)
            when (operationCancellation.IsCancellationRequested)
        {
            SetStatusForConversation(
                selected.Id,
                LanguageRuntime.GetString("Chat.Group.Stopped"));
        }
        catch (Exception exception)
        {
            SetStatusForConversation(
                selected.Id,
                LanguageRuntime.Format("Chat.Group.FailedFormat", LanguageRuntime.ErrorMessage(exception)));
        }
        finally
        {
            if (groupMessagePersisted)
            {
                TriggerGroupAutoMemory(selected.Id);
            }

            _generationSessions.End(selected.Id, operationId);
            RaiseCurrentConversationBusyChanged(selected.Id);
            SendLocalCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task SendAsync(SendSnapshot snapshot)
    {
        var conversationId = snapshot.ConversationId;
        var operationCancellation = _generationSessions.GetCancellationToken(
            conversationId,
            snapshot.OperationId);
        var groupMessagePersisted = false;
        try
        {
            if (snapshot.Input.Length == 0
                || (snapshot.SendMode == ChatSendMode.SendAndGenerate
                    && snapshot.Assignment is null))
            {
                return;
            }

            var speakerId = snapshot.CharacterId;
            if (snapshot.Mode == ConversationMode.Group
                && snapshot.SendMode == ChatSendMode.SendAndGenerate)
            {
                var messages = (await _repository.ListMessagesAsync(
                        conversationId,
                        operationCancellation))
                    .Append(new ChatMessage
                    {
                        ConversationId = conversationId,
                        SequenceNo = long.MaxValue,
                        SenderKind = MessageSenderKind.User,
                        SenderId = "local-user",
                        Content = snapshot.Input
                    })
                    .ToArray();
                var decision = DecideGroupNext(snapshot.Context, messages);
                if (decision.NextSpeakerId is null)
                {
                    SetStatusForConversation(
                        conversationId,
                        LanguageRuntime.GroupRelayReason(decision.Reason));
                    return;
                }

                speakerId = decision.NextSpeakerId;
            }

            var context = await AssembleContextAsync(
                conversationId,
                snapshot.Input,
                historyBeforeSequenceNo: null,
                snapshot: snapshot.Context with { SpeakerCharacterId = speakerId },
                cancellationToken: operationCancellation);
            PublishActualContextBudget(conversationId, context);
            if (!CanSendContext(context))
            {
                SetStatusForConversation(
                    conversationId,
                    LanguageRuntime.GetString("Chat.Send.ContextOverLimit"));
                return;
            }

            var message = new ChatMessage
            {
                ConversationId = conversationId,
                SenderKind = MessageSenderKind.User,
                SenderId = "local-user",
                Content = snapshot.Input
            };
            await _repository.AddMessageAsync(message, operationCancellation);
            groupMessagePersisted = snapshot.Mode == ConversationMode.Group;
            if (SelectedConversation?.Id == conversationId
                && string.Equals(
                    ComposerText.Trim(),
                    snapshot.Input,
                    StringComparison.Ordinal))
            {
                SetComposerTextProgrammatically(string.Empty);
            }

            await ReloadGroupsPreservingSelectionAsync();
            if (snapshot.SendMode == ChatSendMode.SaveOnly)
            {
                SetStatusForConversation(
                    conversationId,
                    LanguageRuntime.GetString("Chat.Send.SaveOnly"));
                return;
            }

            var assignment = snapshot.Assignment!;
            var assistant = await GenerateReplyAsync(
                snapshot,
                assignment,
                context,
                speakerId ?? string.Empty);
            if (assistant is null)
            {
                if (snapshot.Mode == ConversationMode.Group
                    && speakerId is { Length: > 0 }
                    && !IsGenerationInterrupted(conversationId))
                {
                    await PauseGroupRelayForInvalidReplyAsync(
                        conversationId,
                        speakerId,
                        speakerId,
                        0,
                        operationCancellation);
                }
                return;
            }

            await ReloadGroupsPreservingSelectionAsync();
            if (snapshot.Mode == ConversationMode.Group)
            {
                if (_generationCoordinator.GetState(conversationId).Status
                    != ConversationGenerationStatus.Interrupted)
                {
                    await ContinueGroupRelayAsync(snapshot, assistant);
                }

            }
            else if (snapshot.Mode == ConversationMode.SingleCharacter)
            {
                TriggerSingleAutoMemory(snapshot);
            }
        }
        catch (OperationCanceledException)
            when (operationCancellation.IsCancellationRequested)
        {
            SetStatusForConversation(
                conversationId,
                LanguageRuntime.GetString("Chat.Send.Stopped"));
        }
        catch (Exception exception)
        {
            SetStatusForConversation(
                conversationId,
                LanguageRuntime.Format("Chat.Send.FailedFormat", LanguageRuntime.ErrorMessage(exception)));
            await ReloadGroupsPreservingSelectionAsync();
        }
        finally
        {
            if (groupMessagePersisted)
            {
                TriggerGroupAutoMemory(conversationId);
            }

            _generationSessions.End(conversationId, snapshot.OperationId);
            RaiseCurrentConversationBusyChanged(conversationId);
            SendLocalCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task<ChatMessage?> GenerateReplyAsync(
        SendSnapshot snapshot,
        ModelFunctionAssignment assignment,
        ContextAssemblyResult context,
        string speakerId)
    {
        BeginProviderGeneration(snapshot.ConversationId);
        var result = await _chatReplies.ExecuteAsync(
            snapshot.ConversationId,
            snapshot.OperationId,
            speakerId,
            ChatRequestFactory.CreateExecutionRequest(assignment, context, snapshot.ConversationId),
            snapshot.Mode == ConversationMode.Group,
            snapshot.Context.Group?.MemberNames.GetValueOrDefault(speakerId));
        var telemetry = _generationSessions.Get(snapshot.ConversationId);
        var status = result.Outcome switch
        {
            ChatReplyOutcome.Saved => CompletedReplyStatus(
                snapshot.ConversationId, assignment.ModelId, telemetry),
            ChatReplyOutcome.Interrupted when result.HadContent =>
                LanguageRuntime.GetString("Chat.Generation.InterruptedPartial"),
            ChatReplyOutcome.InvalidGroupReply =>
                LanguageRuntime.GetString("Chat.Group.InvalidReply"),
            _ => EmptyReplyStatus(snapshot.ConversationId, telemetry)
        };
        SetStatusForConversation(snapshot.ConversationId, status);
        return result.Message;
    }

    private async Task ContinueGroupRelayAsync(
        SendSnapshot snapshot,
        ChatMessage currentSpeakerMessage)
    {
        var operationCancellation = _generationSessions.GetCancellationToken(
            snapshot.ConversationId,
            snapshot.OperationId);
        using var countdownCancellation = new CancellationTokenSource();
        using var relayDelayCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                operationCancellation,
                countdownCancellation.Token);
        _groupAutoRelayCountdownCancellation = countdownCancellation;
        var group = snapshot.Context.Group
                    ?? throw new InvalidOperationException(
                        LanguageRuntime.GetString("Chat.Group.ContextMissing"));
        var automaticTurns = 0;
        var current = currentSpeakerMessage;
        try
        {
            while (true)
            {
                var messages = await _repository.ListMessagesAsync(
                    snapshot.ConversationId,
                    operationCancellation);
                var decision = DecideGroupNext(snapshot.Context, messages);
                var shouldPause = decision.PauseForUser || decision.NextSpeakerId is null;
                await SaveGroupStateAsync(
                    snapshot.ConversationId,
                    current.SenderId,
                    decision.NextSpeakerId ?? string.Empty,
                    automaticTurns,
                    shouldPause,
                    decision.Reason,
                    operationCancellation);
                if (shouldPause)
                {
                    SetStatusForConversation(
                        snapshot.ConversationId,
                        LanguageRuntime.GroupRelayReason(decision.Reason));
                    return;
                }

                if (!IsGroupAutoContinueEnabled(snapshot.ConversationId))
                {
                    SetStatusForConversation(
                        snapshot.ConversationId,
                        LanguageRuntime.Format(
                            "Chat.Group.AutoRelayOffFormat",
                            LanguageRuntime.GroupRelayReason(decision.Reason)));
                    return;
                }

                if (automaticTurns >= group.Settings.MaximumAutomaticTurns)
                {
                    var reason = LanguageRuntime.GetString("Chat.Group.AutoRelayLimit");
                    await SaveGroupStateAsync(
                        snapshot.ConversationId,
                        current.SenderId,
                        decision.NextSpeakerId!,
                        automaticTurns,
                        isPaused: true,
                        reason,
                        operationCancellation);
                    SetStatusForConversation(snapshot.ConversationId, reason);
                    return;
                }

                if (!await WaitForGroupAutoRelayAsync(
                        snapshot.ConversationId,
                        operationCancellation,
                        relayDelayCancellation.Token))
                {
                    if (operationCancellation.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(operationCancellation);
                    }

                    SetStatusForConversation(
                        snapshot.ConversationId,
                        LanguageRuntime.Format(
                            "Chat.Group.AutoRelayOffFormat",
                            LanguageRuntime.GroupRelayReason(decision.Reason)));
                    return;
                }

                automaticTurns++;
                var context = await AssembleContextAsync(
                    snapshot.ConversationId,
                    userInput: string.Empty,
                    historyBeforeSequenceNo: null,
                    snapshot: snapshot.Context with
                    {
                        SpeakerCharacterId = decision.NextSpeakerId
                    },
                    cancellationToken: operationCancellation);
                PublishActualContextBudget(snapshot.ConversationId, context);
                if (!CanSendContext(context))
                {
                    var reason = LanguageRuntime.GetString("Chat.Group.NextContextOverLimit");
                    await SaveGroupStateAsync(
                        snapshot.ConversationId,
                        current.SenderId,
                        decision.NextSpeakerId!,
                        automaticTurns,
                        isPaused: true,
                        reason,
                        operationCancellation);
                    SetStatusForConversation(snapshot.ConversationId, reason);
                    return;
                }

                var next = await GenerateReplyAsync(
                    snapshot,
                    snapshot.Assignment!,
                    context,
                    decision.NextSpeakerId!);
                if (next is null)
                {
                    if (IsGenerationInterrupted(snapshot.ConversationId))
                    {
                        return;
                    }

                    await PauseGroupRelayForInvalidReplyAsync(
                        snapshot.ConversationId,
                        current.SenderId,
                        decision.NextSpeakerId!,
                        automaticTurns,
                        operationCancellation);
                    return;
                }

                if (_generationCoordinator.GetState(snapshot.ConversationId).Status
                    == ConversationGenerationStatus.Interrupted)
                {
                    return;
                }

                current = next;
                await ReloadGroupsPreservingSelectionAsync();
            }
        }
        finally
        {
            if (ReferenceEquals(
                    _groupAutoRelayCountdownCancellation,
                    countdownCancellation))
            {
                _groupAutoRelayCountdownCancellation = null;
            }

            ClearGroupAutoRelayCountdown();
        }
    }

    private bool IsGroupAutoContinueEnabled(string conversationId) =>
        string.Equals(
            Group.ConversationId,
            conversationId,
            StringComparison.Ordinal)
        && Group.AutoContinueEnabled;

    private async Task<bool> WaitForGroupAutoRelayAsync(
        string conversationId,
        CancellationToken operationCancellation,
        CancellationToken delayCancellation)
    {
        if (_groupAutoRelayDelay <= TimeSpan.Zero)
        {
            return IsGroupAutoContinueEnabled(conversationId);
        }

        var remainingSeconds = Math.Max(
            1,
            (int)Math.Ceiling(_groupAutoRelayDelay.TotalSeconds));
        for (var remaining = remainingSeconds; remaining > 0; remaining--)
        {
            if (!IsGroupAutoContinueEnabled(conversationId))
            {
                ClearGroupAutoRelayCountdown();
                return false;
            }

            SetGroupAutoRelayCountdown(
                LanguageRuntime.Format(
                    "Chat.Group.AutoRelayCountdownFormat",
                    remaining));
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), delayCancellation);
            }
            catch (OperationCanceledException)
            {
                ClearGroupAutoRelayCountdown();
                if (operationCancellation.IsCancellationRequested)
                {
                    throw;
                }

                return false;
            }
        }

        ClearGroupAutoRelayCountdown();
        return IsGroupAutoContinueEnabled(conversationId);
    }

    private void SetGroupAutoRelayCountdown(string text)
    {
        if (_groupAutoRelayCountdownText != text)
        {
            _groupAutoRelayCountdownText = text;
            OnPropertyChanged(nameof(GroupAutoRelayCountdownText));
        }

        if (!_isGroupAutoRelayCountdownVisible)
        {
            _isGroupAutoRelayCountdownVisible = true;
            OnPropertyChanged(nameof(IsGroupAutoRelayCountdownVisible));
            StopGroupAutoRelayCommand.RaiseCanExecuteChanged();
        }
    }

    private void ClearGroupAutoRelayCountdown()
    {
        var changed = _isGroupAutoRelayCountdownVisible;
        _isGroupAutoRelayCountdownVisible = false;
        if (_groupAutoRelayCountdownText.Length > 0)
        {
            _groupAutoRelayCountdownText = string.Empty;
            OnPropertyChanged(nameof(GroupAutoRelayCountdownText));
        }

        if (changed)
        {
            OnPropertyChanged(nameof(IsGroupAutoRelayCountdownVisible));
            StopGroupAutoRelayCommand.RaiseCanExecuteChanged();
        }
    }

    private void StopGroupAutoRelay()
    {
        Group.SuppressAutoContinue();
        _groupAutoRelayCountdownCancellation?.Cancel();
        ClearGroupAutoRelayCountdown();
        StopCurrentGeneration();
    }

    private async Task PauseGroupRelayForInvalidReplyAsync(
        string conversationId,
        string currentSpeakerId,
        string nextSpeakerId,
        int automaticTurns,
        CancellationToken cancellationToken)
    {
        var reason = LanguageRuntime.GetString("Chat.Group.InvalidReply");
        await SaveGroupStateAsync(
            conversationId,
            currentSpeakerId,
            nextSpeakerId,
            automaticTurns,
            isPaused: true,
            reason,
            cancellationToken);
        SetStatusForConversation(conversationId, reason);
    }

    private GroupRelayDecision DecideGroupNext(
        ContextInputSnapshot context,
        IReadOnlyList<ChatMessage> messages)
    {
        var group = context.Group
                    ?? throw new InvalidOperationException(
                        LanguageRuntime.GetString("Chat.Group.SettingsMissing"));
        return _groupRelayPlanner.DecideNext(
            group.Settings,
            group.Members,
            group.MemberNames,
            messages,
            context.PersonaName);
    }

    private async Task SaveGroupStateAsync(
        string conversationId,
        string currentSpeakerId,
        string nextSpeakerId,
        int automaticTurns,
        bool isPaused,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var state = new GroupChatState
        {
            ConversationId = conversationId,
            CurrentSpeakerId = currentSpeakerId,
            NextSpeakerId = nextSpeakerId,
            AutomaticTurns = automaticTurns,
            IsPaused = isPaused,
            PauseReason = reason
        };
        await _groupChats.SaveStateAsync(state, cancellationToken);
        Group.ApplyState(state);
    }

    private void TriggerSingleAutoMemory(SendSnapshot snapshot)
    {
        var ownerId = snapshot.Mode == ConversationMode.SingleCharacter
            ? snapshot.CharacterId
            : null;
        if (ownerId is not null)
        {
            _ = Memory.TryAutoGenerateAsync(ownerId, snapshot.ConversationId);
        }
    }

    private async Task GenerateGroupMergeAsync(
        Character character,
        GroupChatSettings groupSettings)
    {
        var selected = SelectedConversation;
        if (selected?.Mode != ConversationMode.Group
            || !IsSelectionReady(selected.Id)
            || !string.Equals(
                Group.ConversationId,
                selected.Id,
                StringComparison.Ordinal)
            || !string.Equals(
                Memory.ConversationId,
                selected.Id,
                StringComparison.Ordinal)
            || !string.Equals(
                groupSettings.ConversationId,
                selected.Id,
                StringComparison.Ordinal))
        {
            Status = LanguageRuntime.GetString(
                "Memory.GroupMergeConversationMismatch");
            return;
        }

        await Memory.GenerateGroupMergeAsync(character, groupSettings);
    }

    private void OnMemoryBodySaved(
        object? sender,
        MemoryBodySavedEventArgs args)
    {
        if (!MemoryOwnerIds.TryParseGroup(
                args.OwnerId,
                out var conversationId,
                out var characterId)
            || !string.Equals(
                conversationId,
                args.ConversationId,
                StringComparison.Ordinal))
        {
            return;
        }

        _unsavedGroupMemoryBodies.TryRemove(conversationId, out _);
        ClearGroupMemoryInvalid(
            conversationId,
            characterId is null
                ? GroupMemoryScopeMask.Shared
                : GroupMemoryScopeMask.Members);
        ScheduleContextRefresh();
    }

    private void OnMemoryBodyChanged(object? sender, EventArgs args)
    {
        if (Memory.OwnerId is not { } ownerId
            || !MemoryOwnerIds.TryParseGroup(
                ownerId,
                out var conversationId,
                out var characterId)
            || characterId is not null)
        {
            ScheduleContextRefresh();
            return;
        }

        if (Memory.IsBodyDirty)
        {
            _unsavedGroupMemoryBodies[conversationId] = 0;
        }
        else
        {
            _unsavedGroupMemoryBodies.TryRemove(conversationId, out _);
        }

        ScheduleContextRefresh();
    }

    private void ForgetUnsavedGroupMemoryBody()
    {
        if (Memory.ConversationId is { } conversationId)
        {
            _unsavedGroupMemoryBodies.TryRemove(conversationId, out _);
        }
    }

    private void TriggerGroupAutoMemory(
        string conversationId,
        bool invalidateCurrentMemory = false)
    {
        if (invalidateCurrentMemory)
        {
            MarkGroupMemoryInvalid(
                conversationId,
                GroupMemoryScopeMask.All);
            ScheduleContextRefresh();
        }

        _ = TriggerGroupAutoMemoryCoreAsync(conversationId);
    }

    private async Task TriggerGroupAutoMemoryCoreAsync(string conversationId)
    {
        try
        {
            await UpdateGroupMemoryAsync(conversationId, force: false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (Group.ConversationId == conversationId)
            {
                Group.ApplyMemoryUpdateFailure(LanguageRuntime.ErrorMessage(exception));
            }
        }
    }

    private async Task UpdateGroupMemoryAsync(
        string conversationId,
        bool force)
    {
        try
        {
            var result = await _groupMemory.UpdateAsync(
                conversationId,
                force);
            if (Group.ConversationId == conversationId)
            {
                Group.ApplyMemoryUpdateResult(result);
                if (result.Status is GroupMemoryUpdateStatus.Updated
                        or GroupMemoryUpdateStatus.PartiallyUpdated
                    && SelectedConversation?.Id == conversationId)
                {
                    await Memory.LoadAsync(
                        MemoryOwnerIds.ForGroup(conversationId),
                        conversationId,
                        LanguageRuntime.Format(
                            "Chat.Memory.GroupFormat",
                            SelectedConversation.Title),
                        userIdentity: PersonaName);
                }
            }

            if (result.CompletedScopes != GroupMemoryScopeMask.None)
            {
                ClearGroupMemoryInvalid(
                    conversationId,
                    result.CompletedScopes);
                ScheduleContextRefresh();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (Group.ConversationId == conversationId)
            {
                Group.ApplyMemoryUpdateFailure(LanguageRuntime.ErrorMessage(exception));
            }
        }
    }

    private GroupMemoryScopeMask GetInvalidGroupMemoryScopes(
        string conversationId) =>
        _invalidGroupMemoryScopes.GetValueOrDefault(
            conversationId,
            GroupMemoryScopeMask.None);

    private void MarkGroupMemoryInvalid(
        string conversationId,
        GroupMemoryScopeMask scopes) =>
        _invalidGroupMemoryScopes.AddOrUpdate(
            conversationId,
            scopes,
            (_, current) => current | scopes);

    private void ClearGroupMemoryInvalid(
        string conversationId,
        GroupMemoryScopeMask scopes)
    {
        while (_invalidGroupMemoryScopes.TryGetValue(
                   conversationId,
                   out var current))
        {
            var remaining = current & ~scopes;
            if (remaining == GroupMemoryScopeMask.None)
            {
                if (_invalidGroupMemoryScopes.TryRemove(
                        new KeyValuePair<string, GroupMemoryScopeMask>(
                            conversationId,
                            current)))
                {
                    return;
                }
            }
            else if (_invalidGroupMemoryScopes.TryUpdate(
                         conversationId,
                         remaining,
                         current))
            {
                return;
            }
        }
    }

    private ChatMessageItemViewModel CreateMessageItem(
        ChatMessage message,
        IReadOnlyList<MessageCandidate>? candidates = null) =>
        new(
            message,
            EditMessageAsync,
            DeleteMessageAsync,
            ForkMessageAsync,
            RegenerateMessageAsync,
            ContinueGenerationAsync,
            CanContinueGeneration,
            ActivateCandidateAsync,
            candidates ?? [],
            CopyMessage,
            OpenMessageTools,
            senderLabel: message.SenderKind switch
                {
                    MessageSenderKind.Character =>
                        _conversationBrowser.Characters.GetValueOrDefault(message.SenderId)?.Name,
                    MessageSenderKind.User => EffectivePersonaLabel(),
                    _ => null
                },
            personaName: EffectivePersonaLabel(),
            characterName: EffectiveCharacterMacroName(message),
            avatarPath: message.SenderKind == MessageSenderKind.Character
                ? _conversationBrowser.Characters.GetValueOrDefault(message.SenderId)?.AvatarPath
                : null,
            speak: _speech is null ? null : item => { _ = SpeakMessageAsync(item); },
            speechSettings: item => { _ = ConfigureSpeechAsync(item); },
            speechActive: () => _speech?.IsActive == true && _speech.MessageKey == SpeechKey(message),
            speechStatus: () => _speech?.MessageKey == SpeechKey(message) ? _speech.Status : "",
            canSpeak: () => CanSpeakMessage(message));

    private static string SpeechKey(ChatMessage message) => message.ConversationId + ":" + message.Id;

    private void OnSpeechChanged(object? sender, EventArgs args)
    {
        foreach (var item in Messages) item.RefreshSpeech();
    }

    private void StopVisibleSpeech()
    {
        if (SelectedConversation is { } selected) _speech?.StopConversation(selected.Id);
    }

    private bool CanSpeakMessage(ChatMessage message) => !_disposed && _speech is not null
        && ((_speech.IsActive && _speech.MessageKey == SpeechKey(message))
            || (IsSelectionReady(message.ConversationId) && !IsCurrentConversationBusy));

    private async Task SpeakMessageAsync(ChatMessageItemViewModel item)
    {
        if (_speech is null || _disposed || item.SenderKind != MessageSenderKind.Character) return;
        if (_speech.IsActive && _speech.MessageKey == SpeechKey(item.Message))
        { _speech.Stop(); return; }
        if (!CanSpeakMessage(item.Message)) return;
        await _speech.ToggleAsync(SpeechKey(item.Message), item.Message.SenderId, item.DisplayContent);
    }

    private async Task ConfigureSpeechAsync(ChatMessageItemViewModel item)
    {
        if (_speechSettings is null || _disposed) return;
        try
        {
            var settings = await _speechSettings.LoadAsync();
            if (_disposed) return;
            var dialog = new SpeechSettingsDialog(_speechSettings, settings, item.Message.SenderId, item.SenderLabel)
            { Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive) };
            if (dialog.ShowDialog() == true) _speech?.Stop();
        }
        catch { Status = LanguageRuntime.GetString("Speech.SettingsInvalid"); }
    }

    private async Task ActivateCandidateAsync(
        ChatMessageItemViewModel item,
        MessageCandidate candidate)
    {
        _speech?.Stop(SpeechKey(item.Message));
        await _repository.ActivateCandidateAsync(
            item.Id,
            candidate.CandidateIndex);
        item.ApplyCandidate(candidate);
        ScheduleContextRefresh();
        if (SelectedConversation?.Mode == ConversationMode.Group)
        {
            TriggerGroupAutoMemory(
                item.Message.ConversationId,
                invalidateCurrentMemory: true);
        }

        Status = LanguageRuntime.Format(
            "Chat.CandidateSwitchedFormat",
            item.CandidateNavigationLabel);
    }

    private void RefreshPersonaPresentation()
    {
        var label = EffectivePersonaLabel();
        foreach (var message in Messages)
        {
            if (message.SenderKind == MessageSenderKind.User)
            {
                message.UpdateSenderLabel(label);
            }

            message.UpdateTavernNames(
                label,
                EffectiveCharacterMacroName(message.Message));
        }
    }

    private string EffectivePersonaLabel() =>
        string.IsNullOrWhiteSpace(PersonaName)
            ? "USER"
            : PersonaName.Trim();

    private string EffectiveCharacterMacroName(ChatMessage message)
    {
        if (message.SenderKind == MessageSenderKind.Character
            && !string.IsNullOrWhiteSpace(message.SenderId)
            && _conversationBrowser.Characters.TryGetValue(message.SenderId, out var sender)
            && !string.IsNullOrWhiteSpace(sender.Name))
        {
            return sender.Name.Trim();
        }

        var conversationCharacterId = SelectedConversation?.CharacterId;
        return conversationCharacterId is not null
               && _conversationBrowser.Characters.TryGetValue(
                   conversationCharacterId,
                   out var conversationCharacter)
               && !string.IsNullOrWhiteSpace(conversationCharacter.Name)
            ? conversationCharacter.Name.Trim()
            : "角色";
    }

    private bool CanContinueGeneration(ChatMessageItemViewModel item) =>
        SelectedConversation is not null
        && IsSelectionReady(SelectedConversation.Id)
        && !IsCurrentConversationBusy
        && item.SenderKind == MessageSenderKind.Character
        && string.Equals(Messages.LastOrDefault()?.Id, item.Id, StringComparison.Ordinal);

    private void RefreshContinueGenerationCommands()
    {
        foreach (var message in Messages)
        {
            message.ContinueCommand.RaiseCanExecuteChanged();
            message.RefreshSpeech();
        }
    }

    private void OpenMessageTools(ChatMessageItemViewModel selected)
    {
        foreach (var item in Messages.Where(item => item.Id != selected.Id))
        {
            item.CloseTools();
        }
    }

    private async Task EditMessageAsync(ChatMessageItemViewModel item)
    {
        _speech?.Stop(SpeechKey(item.Message));
        item.CloseTools();
        var edited = await _interaction.EditTextAsync(
            LanguageRuntime.GetString("Chat.Message.EditTitle"),
            LanguageRuntime.GetString("Chat.Message.EditPrompt"),
            item.Content);
        if (edited is null || string.Equals(edited.Trim(), item.Content, StringComparison.Ordinal))
        {
            return;
        }

        await _repository.UpdateMessageContentAsync(item.Id, edited);
        item.Message.Content = edited.Trim();
        item.Message.UpdatedAt = DateTimeOffset.Now;
        item.RefreshContent();
        await ReloadGroupsAsync(SelectedConversation?.Id);
        if (SelectedConversation?.Mode == ConversationMode.Group)
        {
            TriggerGroupAutoMemory(
                item.Message.ConversationId,
                invalidateCurrentMemory: true);
        }

        Status = LanguageRuntime.GetString("Chat.Message.Edited");
    }

    private async Task DeleteMessageAsync(ChatMessageItemViewModel item)
    {
        _speech?.Stop(SpeechKey(item.Message));
        item.CloseTools();
        var decision = _interaction.ConfirmMessageDeletion();
        if (decision == DeleteMessageDecision.Cancel)
        {
            return;
        }

        var conversationId = SelectedConversation?.Id;
        await _repository.DeleteMessageAsync(
            item.Id,
            decision == DeleteMessageDecision.SelectedAndFollowing);
        await ReloadGroupsAsync(conversationId);
        if (SelectedConversation?.Mode == ConversationMode.Group
            && conversationId is not null)
        {
            TriggerGroupAutoMemory(
                conversationId,
                invalidateCurrentMemory: true);
        }

        Status = decision == DeleteMessageDecision.SelectedAndFollowing
            ? LanguageRuntime.GetString("Chat.Message.DeletedTail")
            : LanguageRuntime.GetString("Chat.Message.DeletedOne");
    }

    private async Task ForkMessageAsync(ChatMessageItemViewModel item)
    {
        item.CloseTools();
        if (SelectedConversation is null)
        {
            return;
        }

        var fork = await _repository.ForkThroughMessageAsync(SelectedConversation.Id, item.Id);
        await ReloadGroupsAsync(fork.Id);
        Status = LanguageRuntime.GetString("Chat.Fork.Done");
    }

    private async Task RegenerateMessageAsync(ChatMessageItemViewModel item)
    {
        _speech?.Stop(SpeechKey(item.Message));
        item.CloseTools();
        if (SelectedConversation is null
            || !IsSelectionReady(SelectedConversation.Id)
            || item.SenderKind != MessageSenderKind.Character)
        {
            return;
        }

        var conversationId = SelectedConversation.Id;
        var conversationMode = SelectedConversation.Mode;
        var assignment = AssignmentFor(conversationMode);
        if (assignment is null)
        {
            Status = conversationMode == ConversationMode.Group
                ? LanguageRuntime.GetString("Chat.Regenerate.GroupModelUnassigned")
                : LanguageRuntime.GetString("Chat.Regenerate.ChatModelUnassigned");
            return;
        }

        var additionalRequirement =
            await _interaction.PromptRegenerationRequirementAsync();
        if (additionalRequirement is null)
        {
            Status = LanguageRuntime.GetString("Chat.Regenerate.Cancelled");
            return;
        }

        if (SelectedConversation?.Id != conversationId)
        {
            return;
        }

        if (!_generationSessions.TryBegin(
                conversationId,
                out var operationId))
        {
            Status = LanguageRuntime.GetString("Chat.Generation.AlreadyRunning");
            return;
        }

        RaiseCurrentConversationBusyChanged(conversationId);
        var operationCancellation = _generationSessions.GetCancellationToken(
            conversationId,
            operationId);

        var original = item.Message.Content;
        var originalCandidateIndex = item.Message.ActiveCandidateIndex;
        var contextSnapshot = CreateContextSnapshot(BudgetFor(conversationMode))
            with { SpeakerCharacterId = item.Message.SenderId };
        try
        {
            var conversationMessages = await _repository.ListMessagesAsync(
                conversationId,
                operationCancellation);
            var precedingMessage = conversationMessages
                .Where(message => message.SequenceNo < item.Message.SequenceNo)
                .OrderByDescending(message => message.SequenceNo)
                .FirstOrDefault();
            var continuationInstruction =
                conversationMode == ConversationMode.SingleCharacter
                && precedingMessage?.SenderKind == MessageSenderKind.Character
                    ? AppendAdditionalRequirement(
                        ContinueWithoutUserInstruction,
                        additionalRequirement)
                    : null;
            var regenerationInput = continuationInstruction is null
                ? FormatAdditionalRequirement(additionalRequirement)
                : string.Empty;
            var context = await AssembleContextAsync(
                conversationId,
                userInput: regenerationInput,
                historyBeforeSequenceNo: item.Message.SequenceNo,
                snapshot: contextSnapshot,
                continuationInstruction: continuationInstruction,
                cancellationToken: operationCancellation);
            PublishActualContextBudget(conversationId, context);
            if (!CanSendContext(context))
            {
                Status = LanguageRuntime.GetString("Chat.Regenerate.ContextOverLimit");
                return;
            }

            _generationSessions.BeginReply(
                conversationId,
                operationId,
                item.Id,
                item.Message.SenderId,
                LiveReplyKind.CandidateReplacement);
            BeginProviderGeneration(conversationId);
            var buffer = new System.Text.StringBuilder();
            await _generationCoordinator.RunAsync(
                conversationId,
                token => _chatReplies.StreamContentAsync(
                    conversationId,
                    operationId,
                    ChatRequestFactory.CreateExecutionRequest(
                        assignment,
                        context,
                        conversationId),
                    token),
                (chunk, _) =>
                {
                    buffer.Append(chunk);
                    item.Message.Content = buffer.ToString();
                    item.RefreshContent();
                    return ValueTask.CompletedTask;
                },
                operationCancellation);
            var telemetry = _generationSessions.Get(conversationId);
            if (IsGenerationInterrupted(conversationId))
            {
                item.Message.Content = original;
                item.RefreshContent();
                SetStatusForConversation(
                    conversationId,
                    buffer.Length == 0
                        ? EmptyReplyStatus(conversationId, telemetry, isCandidate: true)
                        : LanguageRuntime.GetString("Chat.Regenerate.Stopped"));
                return;
            }

            if (buffer.Length == 0)
            {
                item.Message.Content = original;
                item.RefreshContent();
                SetStatusForConversation(
                    conversationId,
                    EmptyReplyStatus(conversationId, telemetry, isCandidate: true));
                return;
            }

            var generatedContent = buffer.ToString();
            if (conversationMode == ConversationMode.Group)
            {
                var expectedSpeakerName = contextSnapshot.Group?.MemberNames
                    .GetValueOrDefault(item.Message.SenderId);
                var normalized = GroupRelayResponseNormalizer.Normalize(
                    generatedContent,
                    expectedSpeakerName);
                if (!normalized.IsValid)
                {
                    item.Message.Content = original;
                    item.RefreshContent();
                    SetStatusForConversation(
                        conversationId,
                        LanguageRuntime.GetString("Chat.Group.InvalidReply"));
                    return;
                }

                generatedContent = normalized.Content;
            }

            var candidates = await _repository.ListCandidatesAsync(item.Id);
            if (candidates.Count == 0)
            {
                var originalCandidate = new MessageCandidate
                {
                    MessageId = item.Id,
                    CandidateIndex = originalCandidateIndex,
                    Content = original
                };
                await _repository.AddCandidateAsync(originalCandidate);
                candidates = [originalCandidate];
            }

            var nextIndex =
                candidates.Max(candidate => candidate.CandidateIndex) + 1;
            await _repository.AddAndActivateCandidateAsync(new MessageCandidate
            {
                MessageId = item.Id,
                CandidateIndex = nextIndex,
                Content = generatedContent
            });
            item.Message.Content = generatedContent;
            item.Message.ActiveCandidateIndex = nextIndex;
            item.RefreshContent();
            await ReloadGroupsPreservingSelectionAsync();
            if (conversationMode == ConversationMode.Group)
            {
                TriggerGroupAutoMemory(
                    conversationId,
                    invalidateCurrentMemory: true);
            }

            var generationInterrupted =
                _generationCoordinator.GetState(conversationId).Status
                == ConversationGenerationStatus.Interrupted;
            var suffix = string.Equals(
                telemetry.FinishReason,
                "length",
                StringComparison.OrdinalIgnoreCase)
                ? LanguageRuntime.GetString("Chat.Regenerate.OutputLimitSuffix")
                : string.Empty;
            SetStatusForConversation(
                conversationId,
                generationInterrupted
                    ? LanguageRuntime.Format(
                        "Chat.Regenerate.InterruptedFormat",
                        nextIndex + 1)
                    : LanguageRuntime.Format(
                        "Chat.Regenerate.DoneFormat",
                        nextIndex + 1,
                        suffix));
        }
        catch (OperationCanceledException)
            when (operationCancellation.IsCancellationRequested)
        {
            item.Message.Content = original;
            item.RefreshContent();
            SetStatusForConversation(
                conversationId,
                LanguageRuntime.GetString("Chat.Regenerate.Stopped"));
        }
        catch (Exception exception)
        {
            item.Message.Content = original;
            item.RefreshContent();
            SetStatusForConversation(
                conversationId,
                LanguageRuntime.Format("Chat.Regenerate.FailedFormat", LanguageRuntime.ErrorMessage(exception)));
        }
        finally
        {
            _generationSessions.End(conversationId, operationId);
            RaiseCurrentConversationBusyChanged(conversationId);
            SendLocalCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task ContinueGenerationAsync(ChatMessageItemViewModel item)
    {
        item.CloseTools();
        var selected = SelectedConversation;
        if (selected is null || !CanContinueGeneration(item))
        {
            return;
        }

        if (selected.Mode == ConversationMode.Group)
        {
            await StartGroupContinueAsync(manualSpeakerId: null);
            return;
        }

        var assignment = _chatAssignment;
        if (assignment is null)
        {
            Status = LanguageRuntime.GetString("Chat.Continue.ModelUnassigned");
            return;
        }

        if (!_generationSessions.TryBegin(selected.Id, out var operationId))
        {
            Status = LanguageRuntime.GetString("Chat.Generation.AlreadyRunningShort");
            return;
        }

        var snapshot = new SendSnapshot(
            selected.Id,
            ConversationMode.SingleCharacter,
            selected.CharacterId,
            Input: string.Empty,
            ChatSendMode.SendAndGenerate,
            assignment,
            CreateContextSnapshot(BudgetFor(ConversationMode.SingleCharacter)),
            operationId);
        RaiseCurrentConversationBusyChanged(selected.Id);
        var operationCancellation = _generationSessions.GetCancellationToken(
            selected.Id,
            operationId);
        try
        {
            var context = await AssembleContextAsync(
                selected.Id,
                userInput: string.Empty,
                historyBeforeSequenceNo: null,
                snapshot: snapshot.Context,
                continuationInstruction: ContinueWithoutUserInstruction,
                cancellationToken: operationCancellation);
            PublishActualContextBudget(selected.Id, context);
            if (!CanSendContext(context))
            {
                Status = LanguageRuntime.GetString("Chat.Continue.ContextOverLimit");
                return;
            }

            var assistant = await GenerateReplyAsync(
                snapshot,
                assignment,
                context,
                selected.CharacterId ?? item.Message.SenderId);
            if (assistant is null)
            {
                return;
            }

            await ReloadGroupsPreservingSelectionAsync();
            TriggerSingleAutoMemory(snapshot);
        }
        catch (OperationCanceledException)
            when (operationCancellation.IsCancellationRequested)
        {
            SetStatusForConversation(
                selected.Id,
                LanguageRuntime.GetString("Chat.Continue.Stopped"));
        }
        catch (Exception exception)
        {
            SetStatusForConversation(
                selected.Id,
                LanguageRuntime.Format("Chat.Continue.FailedFormat", LanguageRuntime.ErrorMessage(exception)));
            await ReloadGroupsPreservingSelectionAsync();
        }
        finally
        {
            _generationSessions.End(selected.Id, operationId);
            RaiseCurrentConversationBusyChanged(selected.Id);
            SendLocalCommand.RaiseCanExecuteChanged();
        }
    }

    private void CopyMessage(ChatMessageItemViewModel item)
    {
        item.CloseTools();
        _interaction.CopyText(item.DisplayContent);
        Status = LanguageRuntime.GetString("Chat.Message.Copied");
    }

    private void StopCurrentGeneration()
    {
        if (SelectedConversation is null)
        {
            return;
        }

        var conversationId = SelectedConversation.Id;
        var stopped = _generationSessions.Cancel(conversationId);
        stopped = _generationCoordinator.Cancel(conversationId) || stopped;
        if (stopped)
        {
            Status = LanguageRuntime.GetString("Chat.Generation.StoppingCurrent");
        }
    }

    private async Task LoadPersonaAsync()
    {
        var displayMode = await _settings.GetAsync("chat.displayMode");
        if (Enum.TryParse<ChatDisplayMode>(displayMode, out var parsedDisplayMode))
        {
            _displayMode = parsedDisplayMode;
            OnPropertyChanged(nameof(DisplayMode));
            OnPropertyChanged(nameof(IsNovelMode));
        }

        await _personas.LoadAsync();
        ApplyActivePersona();
        PersonaStatus = _personas.Status;
        GlobalPreset = _globalPrompts.Get(GlobalPromptKey.ChatSystem);
    }

    private async Task SaveDisplayModeAsync(ChatDisplayMode displayMode)
    {
        try
        {
            await _settings.SetAsync("chat.displayMode", displayMode.ToString());
        }
        catch (Exception exception)
        {
            Status = LanguageRuntime.Format("Chat.Display.SaveFailedFormat", LanguageRuntime.ErrorMessage(exception));
        }
    }

    private async Task SavePersonaAsync()
    {
        await _personas.SaveCurrentAsync();
        ApplyActivePersona();
        PersonaStatus = _personas.Status;
        await RefreshContextEstimateAsync(immediate: true);
    }

    private void CancelPersonaEdits()
    {
        _personas.CancelEdits();
        ApplyActivePersona();
        PersonaStatus = _personas.Status;
    }

    private bool CanEditCharacterPrompt() =>
        IsSingleCharacterConversation
        && !string.IsNullOrWhiteSpace(_characterPromptCharacterId);

    private Task EditCharacterSystemPromptAsync() =>
        EditCharacterPromptAsync(editPostHistory: false);

    private Task EditCharacterPostHistoryAsync() =>
        EditCharacterPromptAsync(editPostHistory: true);

    private async Task EditCharacterPromptAsync(bool editPostHistory)
    {
        var characterId = _characterPromptCharacterId;
        var conversationId = SelectedConversation?.Id;
        if (!CanEditCharacterPrompt()
            || string.IsNullOrWhiteSpace(characterId)
            || string.IsNullOrWhiteSpace(conversationId))
        {
            return;
        }

        try
        {
            var character = await _characters.GetAsync(characterId);
            if (character is null)
            {
                CharacterPromptStatus = LanguageRuntime.GetString("Chat.CharacterPrompt.Missing");
                return;
            }

            var buffer = new CharacterEditBuffer();
            buffer.Load(character);
            var currentText = editPostHistory
                ? buffer.PostHistoryInstructions
                : buffer.SystemPrompt;
            var edited = await _interaction.EditTextAsync(
                editPostHistory
                    ? LanguageRuntime.Format(
                        "Chat.CharacterPrompt.EditPostHistoryFormat",
                        character.Name)
                    : LanguageRuntime.Format(
                        "Chat.CharacterPrompt.EditSystemFormat",
                        character.Name),
                editPostHistory
                    ? LanguageRuntime.GetString("Chat.CharacterPrompt.PostHistoryHelp")
                    : LanguageRuntime.GetString("Chat.CharacterPrompt.SystemHelp"),
                currentText);
            if (edited is null
                || string.Equals(edited, currentText, StringComparison.Ordinal))
            {
                return;
            }

            if (editPostHistory)
            {
                buffer.PostHistoryInstructions = edited;
            }
            else
            {
                buffer.SystemPrompt = edited;
            }

            buffer.ApplyTo(character);
            character.UpdatedAt = DateTimeOffset.Now;
            await _characters.UpsertAsync(character);
            _conversationBrowser.UpdateCharacter(character);

            if (SelectedConversation?.Id == conversationId)
            {
                ApplyCharacterPrompts(character);
                CharacterPromptStatus = editPostHistory
                    ? LanguageRuntime.GetString("Chat.CharacterPrompt.PostHistorySaved")
                    : LanguageRuntime.GetString("Chat.CharacterPrompt.SystemSaved");
                await RefreshContextEstimateAsync(immediate: true);
            }
        }
        catch (Exception exception)
        {
            CharacterPromptStatus = LanguageRuntime.Format(
                "Chat.CharacterPrompt.SaveFailedFormat",
                LanguageRuntime.ErrorMessage(exception));
        }
    }

    private void ApplyCharacterPrompts(Character? character)
    {
        if (character is null)
        {
            _characterPromptCharacterId = string.Empty;
            CharacterPromptCharacterName = LanguageRuntime.GetString("Chat.Character.None");
            CharacterSystemPrompt = string.Empty;
            CharacterPostHistoryInstructions = string.Empty;
            CharacterPromptStatus =
                LanguageRuntime.GetString("Chat.CharacterPrompt.Select");
        }
        else
        {
            var buffer = new CharacterEditBuffer();
            buffer.Load(character);
            _characterPromptCharacterId = character.Id;
            CharacterPromptCharacterName = character.Name;
            CharacterSystemPrompt = buffer.SystemPrompt;
            CharacterPostHistoryInstructions =
                buffer.PostHistoryInstructions;
            CharacterPromptStatus =
                string.IsNullOrWhiteSpace(buffer.SystemPrompt)
                && string.IsNullOrWhiteSpace(buffer.PostHistoryInstructions)
                    ? LanguageRuntime.GetString("Chat.CharacterPrompt.Empty")
                    : LanguageRuntime.GetString("Chat.CharacterPrompt.FromCard");
        }

        EditCharacterSystemPromptCommand.RaiseCanExecuteChanged();
        EditCharacterPostHistoryCommand.RaiseCanExecuteChanged();
    }

    private async Task OpenGlobalPromptAsync(object? parameter)
    {
        if (OpenPromptSettings is null
            || parameter is not string keyText
            || !Enum.TryParse<GlobalPromptKey>(keyText, out var key))
        {
            Status = LanguageRuntime.GetString("Chat.GlobalPrompt.Unavailable");
            return;
        }

        await OpenPromptSettings(key);
    }

    private async Task RefreshAssignmentsAsync()
    {
        var chatTask = _modelAssignments.GetAsync(ModelFunctionKind.Chat);
        var groupTask = _modelAssignments.GetAsync(ModelFunctionKind.GroupChat);
        await Task.WhenAll(chatTask, groupTask);
        _chatAssignment = chatTask.Result;
        _groupChatAssignment = groupTask.Result;
        ApplyActiveAssignmentBudget(
            SelectedConversation?.Mode ?? ConversationMode.SingleCharacter);

        SendLocalCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(EstimatedTokenText));
    }

    private ModelFunctionAssignment? AssignmentFor(ConversationMode mode) =>
        mode == ConversationMode.Group ? _groupChatAssignment : _chatAssignment;

    private ContextBudget CurrentUiBudget =>
        BudgetFor(SelectedConversation?.Mode ?? ConversationMode.SingleCharacter);

    private ContextBudget BudgetFor(ConversationMode mode)
    {
        var assignment = AssignmentFor(mode);
        var functionName = mode == ConversationMode.Group
            ? LanguageRuntime.GetString("Chat.Function.Group")
            : LanguageRuntime.GetString("Chat.Function.Character");
        if (assignment is null)
        {
            return new ContextBudget(
                32768,
                4096,
                LanguageRuntime.Format("Chat.Model.FunctionUnassignedFormat", functionName));
        }

        return new ContextBudget(
            assignment.ContextLimit,
            assignment.MaxOutputTokens,
            $"{assignment.ProviderId} / {assignment.ModelId}",
            assignment.ModelId);
    }

    private bool IsGenerationInterrupted(string conversationId) =>
        _generationCoordinator.GetState(conversationId).Status
        == ConversationGenerationStatus.Interrupted;

    private void ApplyActiveAssignmentBudget(ConversationMode mode)
    {
        var assignment = AssignmentFor(mode);
        var functionName = mode == ConversationMode.Group
            ? LanguageRuntime.GetString("Chat.Function.Group")
            : LanguageRuntime.GetString("Chat.Function.Character");
        var budget = BudgetFor(mode);
        _contextBudget.UpdateBudget(budget);
        _contextPreview.UpdateBudgetSource(budget);
        if (assignment is null)
        {
            ActiveModelText =
                LanguageRuntime.Format(
                    "Chat.Model.FunctionUnassignedSaveOnlyFormat",
                    functionName);
        }
        else
        {
            ActiveModelText =
                LanguageRuntime.Format(
                    "Chat.Model.ActiveFormat",
                    functionName,
                    assignment.ModelId,
                    assignment.ContextLimit,
                    assignment.MaxOutputTokens);
        }

        OnPropertyChanged(nameof(EstimatedTokenText));
    }

    private void ScheduleContextRefresh() => _ = RefreshContextEstimateAsync(immediate: false);

    private Task RefreshContextEstimateAsync(bool immediate, CancellationToken cancellationToken = default)
    {
        if (_disposed || SelectedConversation is not { } selected || !IsSelectionReady(selected.Id))
            return Task.CompletedTask;
        var request = CreateContextRequest(selected.Id, ComposerText, null,
            CreateContextSnapshot(CurrentUiBudget), allowRemoteSemanticRetrieval: false);
        return _contextPreview.RefreshAsync(request, immediate, cancellationToken);
    }

    private void OnContextPreviewPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_disposed) return;
        if (args.PropertyName == nameof(ChatContextPreviewViewModel.ErrorStatus))
        {
            if (_contextPreview.ErrorStatus.Length > 0) Status = _contextPreview.ErrorStatus;
            return;
        }
        OnPropertyChanged(args.PropertyName);
        if (args.PropertyName == nameof(IsEstimatedOverLimit)) SendLocalCommand.RaiseCanExecuteChanged();
    }

    private void OnContextPreviewApplied(object? sender, ContextAssemblyResult result)
    {
        if (_disposed) return;
        Retrieval.UpdateFromContext(result);
        SendLocalCommand.RaiseCanExecuteChanged();
    }

    private Task<ContextAssemblyResult> AssembleContextAsync(
        string conversationId,
        string userInput,
        long? historyBeforeSequenceNo,
        ContextInputSnapshot snapshot,
        CancellationToken cancellationToken = default,
        string? continuationInstruction = null,
        bool allowRemoteSemanticRetrieval = true)
    {
        return _contextAssembler.AssembleAsync(
            CreateContextRequest(conversationId, userInput, historyBeforeSequenceNo,
                snapshot, continuationInstruction, allowRemoteSemanticRetrieval), cancellationToken);
    }

    private ContextAssemblyRequest CreateContextRequest(string conversationId, string userInput,
        long? historyBeforeSequenceNo, ContextInputSnapshot snapshot,
        string? continuationInstruction = null, bool allowRemoteSemanticRetrieval = true) =>
        ChatRequestFactory.CreateContextRequest(conversationId, userInput, historyBeforeSequenceNo,
            snapshot, GetInvalidGroupMemoryScopes(conversationId),
            _unsavedGroupMemoryBodies.ContainsKey(conversationId), continuationInstruction,
            allowRemoteSemanticRetrieval);

    private ContextInputSnapshot CreateContextSnapshot(
        ContextBudget budget,
        string? manualSpeakerId = null)
    {
        GroupContextSnapshot? group = null;
        var selected = SelectedConversation;
        var ready = selected is not null && IsSelectionReady(selected.Id);
        if (ready
            && selected?.Mode == ConversationMode.Group
            && string.Equals(
                Group.ConversationId,
                selected.Id,
                StringComparison.Ordinal)
            && string.Equals(
                Memory.ConversationId,
                selected.Id,
                StringComparison.Ordinal))
        {
            var groupSettings = Group.SettingsSnapshot();
            if (string.Equals(
                    groupSettings.GroupSystemPrompt,
                    GroupPromptDefaults.SystemPrompt,
                    StringComparison.Ordinal))
            {
                groupSettings.GroupSystemPrompt =
                    _globalPrompts.Get(GlobalPromptKey.GroupRelaySystem);
            }

            group = new GroupContextSnapshot(
                groupSettings,
                Group.SnapshotMembers(),
                new Dictionary<string, string>(Group.MemberNames, StringComparer.Ordinal),
                manualSpeakerId ?? Group.SelectedNextSpeaker?.Character.Id);
        }

        return
        new(
            budget.ContextLimit,
            budget.ReservedOutputTokens,
            budget.ModelId,
            ready ? Memory.Body : string.Empty,
            PersonaName,
            PersonaDescription,
            Presets.EffectiveSystemPrompt(
                _globalPrompts.Get(GlobalPromptKey.ChatSystem)),
            SpeakerCharacterId: group?.ManualSpeakerId,
            Group: group,
            Retrieval: Retrieval.Snapshot());
    }

    private async Task ReloadGroupsPreservingSelectionAsync()
    {
        await _conversationBrowser.ReloadAsync(() => SelectedConversation?.Id, ApplyConversationFilter);
        // Relay and save continuations need the restored memory/group state, not only the list selection.
        await _selectionLoadTask;
    }

    private static string FormatAdditionalRequirement(string requirement) =>
        string.IsNullOrWhiteSpace(requirement)
            ? string.Empty
            : $"附加要求：{requirement.Trim()}";

    private static string AppendAdditionalRequirement(
        string instruction,
        string requirement)
    {
        var additional = FormatAdditionalRequirement(requirement);
        return additional.Length == 0
            ? instruction
            : $"{instruction}\n{additional}";
    }

    private void BeginProviderGeneration(string conversationId)
    {
        SetStatusForConversation(
            conversationId,
            LanguageRuntime.GetString("Chat.Generation.Waiting"));
    }

    private string EmptyReplyStatus(
        string conversationId,
        ConversationGenerationSession telemetry,
        bool isCandidate = false)
    {
        if (_generationCoordinator.GetState(conversationId).Status
            == ConversationGenerationStatus.Interrupted)
        {
            return telemetry.SawReasoning
                ? LanguageRuntime.GetString("Chat.Generation.StoppedAfterThinking")
                : LanguageRuntime.GetString("Chat.Generation.StoppedNoBody");
        }

        if (string.Equals(
                telemetry.FinishReason,
                "length",
                StringComparison.OrdinalIgnoreCase)
            && telemetry.SawReasoning)
        {
            return LanguageRuntime.GetString("Chat.Generation.OutputLimitNoBody");
        }

        return isCandidate
            ? LanguageRuntime.GetString("Chat.Generation.NoCandidate")
            : LanguageRuntime.GetString("Chat.Generation.NoBody");
    }

    private string CompletedReplyStatus(
        string conversationId,
        string modelId,
        ConversationGenerationSession telemetry)
    {
        if (_generationCoordinator.GetState(conversationId).Status
            == ConversationGenerationStatus.Interrupted)
        {
            return LanguageRuntime.GetString("Chat.Generation.InterruptedPartial");
        }

        return string.Equals(
            telemetry.FinishReason,
            "length",
            StringComparison.OrdinalIgnoreCase)
            ? LanguageRuntime.GetString("Chat.Generation.SavedAtLimit")
            : LanguageRuntime.Format("Chat.Generation.CompletedFormat", modelId);
    }

    private void SetStatusForConversation(string conversationId, string value)
    {
        _conversationStatuses[conversationId] = value;
        if (SelectedConversation?.Id == conversationId)
        {
            Status = value;
        }
    }

    private static string FinishReasonLabel(string? finishReason) =>
        finishReason?.ToLowerInvariant() switch
        {
            "stop" => LanguageRuntime.GetString("Chat.Finish.Stop"),
            "length" => LanguageRuntime.GetString("Chat.Finish.Length"),
            "content_filter" => LanguageRuntime.GetString("Chat.Finish.ContentFilter"),
            "tool_calls" => LanguageRuntime.GetString("Chat.Finish.ToolCalls"),
            null or "" => LanguageRuntime.GetString("Chat.Finish.NotReported"),
            _ => LanguageRuntime.Format("Chat.Finish.OtherFormat", finishReason)
        };

    private void PublishActualContextBudget(string conversationId, ContextAssemblyResult result) =>
        _contextPreview.PublishActualBudget(conversationId, result);

    private void SetComposerTextProgrammatically(string value)
    {
        _isProgrammaticComposerChange = true;
        try
        {
            ComposerText = value;
        }
        finally
        {
            _isProgrammaticComposerChange = false;
        }
    }

    private static bool CanSendContext(ContextAssemblyResult context) =>
        !context.Estimate.ExceedsLimit
        && context.GroupBudget?.CanSend != false;

    private void OnGenerationStateChanged(object? sender, ConversationGenerationState state)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => ApplyGenerationState(state));
            return;
        }

        ApplyGenerationState(state);
    }

    private void ApplyGenerationSession(ConversationGenerationSession session)
    {
        if (session.IsBusy) _speech?.StopConversation(session.ConversationId);
        // Live progress belongs to the shared session; each window renders it
        // without being called back by the reply executor.
        if (session.IsThinking)
            SetStatusForConversation(session.ConversationId, LanguageRuntime.GetString("Chat.Generation.Thinking"));
        else if (session.IsBusy && session.SawContent)
            SetStatusForConversation(session.ConversationId, LanguageRuntime.GetString("Chat.Generation.Receiving"));

        if (SelectedConversation?.Id != session.ConversationId)
        {
            return;
        }

        OnPropertyChanged(nameof(IsModelThinking));
        OnPropertyChanged(nameof(LastGenerationUsageText));
        OnPropertyChanged(nameof(IsCurrentConversationBusy));
        OnPropertyChanged(nameof(CanEditGroupMembers));
        Group.RefreshGenerationState();
        SendLocalCommand.RaiseCanExecuteChanged();
        RefreshContinueGenerationCommands();
        ApplyLiveSession(session);
        if (!session.IsBusy && session.OperationId is not null)
        {
            ScheduleCompletedSessionReload(
                session.ConversationId,
                session.OperationId);
        }
    }

    private void ApplyLiveSession(ConversationGenerationSession session)
    {
        if (SelectedConversation?.Id != session.ConversationId
            || !session.IsBusy
            || session.MessageId is null
            || session.PartialContent.Length == 0)
        {
            return;
        }

        var item = Messages.FirstOrDefault(message =>
            message.Id == session.MessageId);
        if (item is null)
        {
            if (session.ReplyKind != LiveReplyKind.NewMessage)
            {
                return;
            }

            // Streaming new replies appear immediately, but this transient item
            // is only a projection; repository reload supplies the committed row.
            var transient = new ChatMessage
            {
                Id = session.MessageId,
                ConversationId = session.ConversationId,
                SenderKind = MessageSenderKind.Character,
                SenderId = session.SenderId ?? string.Empty,
                Content = session.PartialContent,
                ActiveCandidateIndex = 0
            };
            Messages.Add(CreateMessageItem(transient));
            RefreshContinueGenerationCommands();
            return;
        }

        if (!string.Equals(item.Message.Content, session.PartialContent, StringComparison.Ordinal))
        {
            item.Message.Content = session.PartialContent;
            item.RefreshContent();
        }
    }

    private void ScheduleCompletedSessionReload(
        string conversationId,
        string operationId)
    {
        if (!_pendingSessionRefreshes.TryAdd(conversationId, 0))
        {
            return;
        }

        RaiseCurrentConversationBusyChanged(conversationId);
        SendLocalCommand.RaiseCanExecuteChanged();
        _ = ReloadAfterCompletedSessionAsync(conversationId, operationId);
    }

    private async Task ReloadAfterCompletedSessionAsync(
        string conversationId,
        string operationId)
    {
        try
        {
            var current = _generationSessions.Get(conversationId);
            // A new operation may start before this queued reload runs. Only the
            // operation that actually reached terminal state may refresh the view.
            if (current.IsBusy
                || !string.Equals(
                    current.OperationId,
                    operationId,
                    StringComparison.Ordinal))
            {
                return;
            }

            await ReloadGroupsPreservingSelectionAsync();
        }
        catch (Exception exception)
        {
            SetStatusForConversation(
                conversationId,
                LanguageRuntime.Format(
                    "Chat.RefreshCompletedFailedFormat",
                    LanguageRuntime.ErrorMessage(exception)));
        }
        finally
        {
            _pendingSessionRefreshes.TryRemove(conversationId, out _);
            RaiseCurrentConversationBusyChanged(conversationId);
            SendLocalCommand.RaiseCanExecuteChanged();
        }
    }

    private void ApplyGenerationState(ConversationGenerationState state)
    {
        var item = FindConversation(state.ConversationId);
        item?.ApplyGenerationState(state);
        if (SelectedConversation?.Id == state.ConversationId)
        {
            OnPropertyChanged(nameof(IsCurrentConversationGenerating));
            OnPropertyChanged(nameof(IsCurrentConversationBusy));
            OnPropertyChanged(nameof(CanEditGroupMembers));
            Group.RefreshGenerationState();
            StopGenerationCommand.RaiseCanExecuteChanged();
            SendLocalCommand.RaiseCanExecuteChanged();
            RefreshContinueGenerationCommands();
        }
    }

    private void RaiseCurrentConversationBusyChanged(string conversationId)
    {
        if (SelectedConversation?.Id == conversationId)
        {
            OnPropertyChanged(nameof(IsCurrentConversationBusy));
            OnPropertyChanged(nameof(IsCurrentConversationGenerating));
            OnPropertyChanged(nameof(CanEditGroupMembers));
            Group.RefreshGenerationState();
            StopGenerationCommand.RaiseCanExecuteChanged();
            RefreshContinueGenerationCommands();
        }
    }

    public void Dispose() => BeginDispose();

    public async ValueTask DisposeAsync()
    {
        BeginDispose();
        await Task.WhenAll(_selectionLoadTask, _contextPreview.DisposeAsync().AsTask());
    }

    private void BeginDispose()
    {
        if (_disposed)
        {
            return;
        }

        // Disposing a window releases UI subscriptions and local preview loads;
        // provider generation is application-owned and continues for other views.
        _disposed = true;
        StopVisibleSpeech();
        if (_speech is not null) _speech.Changed -= OnSpeechChanged;
        _sessionUpdates.Dispose();
        _generationCoordinator.StateChanged -= OnGenerationStateChanged;
        _personas.PropertyChanged -= OnPersonaManagerPropertyChanged;
        Memory.BodyChanged -= OnMemoryBodyChanged;
        Memory.BodySaved -= OnMemoryBodySaved;
        _selectionCancellation?.Cancel();
        _selectionCancellation?.Dispose();
        _contextPreview.PropertyChanged -= OnContextPreviewPropertyChanged;
        _contextPreview.PreviewApplied -= OnContextPreviewApplied;
        _contextPreview.Dispose();
    }

    private sealed record SendSnapshot(
        string ConversationId,
        ConversationMode Mode,
        string? CharacterId,
        string Input,
        ChatSendMode SendMode,
        ModelFunctionAssignment? Assignment,
        ContextInputSnapshot Context,
        string OperationId);
}

public sealed record ChatSendModeOption(
    ChatSendMode Value,
    string Label);

public sealed record ChatDisplayModeOption(
    ChatDisplayMode Value,
    string Label);
