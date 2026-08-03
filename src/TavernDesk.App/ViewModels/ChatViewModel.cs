using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using TavernDesk.App.Presentation;
using TavernDesk.App.Services;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;

namespace TavernDesk.App.ViewModels;

public sealed class ChatViewModel : ViewModelBase, IDisposable, IAsyncDisposable
{
    private readonly IConversationRepository _repository;
    private readonly ICharacterRepository _characters;
    private readonly IContextAssembler _contextAssembler;
    private readonly IContextBudgetProvider _contextBudget;
    private readonly IConversationGenerationCoordinator _generationCoordinator;
    private readonly IConversationGenerationSessionStore _generationSessions;
    private readonly IModelAssignmentRepository _modelAssignments;
    private readonly IProviderGateway _providerGateway;
    private readonly IAppSettingsRepository _settings;
    private readonly IGlobalPromptConfiguration _globalPrompts;
    private readonly IUserInteractionService _interaction;
    private readonly IChatArchiveService _chatArchives;
    private readonly IFileDialogService _fileDialog;
    private readonly IGroupChatRepository _groupChats;
    private readonly IGroupRelayPlanner _groupRelayPlanner;
    private readonly ConcurrentDictionary<string, string> _conversationStatuses = new();
    private readonly ConcurrentDictionary<string, byte> _pendingSessionRefreshes = new();
    private readonly SemaphoreSlim _groupReloadGate = new(1, 1);
    private readonly List<CharacterConversationGroupViewModel> _allGroups = [];
    private readonly Dictionary<string, Character> _characterLookup =
        new(StringComparer.Ordinal);
    private readonly Func<string, Task>? _openConversationWindow;
    private ConversationListItemViewModel? _selectedConversation;
    private CancellationTokenSource? _selectionCancellation;
    private CancellationTokenSource? _contextCancellation;
    private Task _selectionLoadTask = Task.CompletedTask;
    private Task _contextRefreshTask = Task.CompletedTask;
    private long _selectionVersion;
    private long _contextVersion;
    private string _conversationSearchText = string.Empty;
    private string _composerText = string.Empty;
    private string _status = "尚未连接模型。当前消息仅保存到本地。";
    private string _personaName = "USER";
    private string _personaDescription = string.Empty;
    private string _globalPreset = string.Empty;
    private string _personaStatus = "Persona 会注入当前聊天上下文；保存后对全部新请求生效。";
    private string _characterPromptCharacterId = string.Empty;
    private string _characterPromptCharacterName = "未选择角色";
    private string _characterSystemPrompt = string.Empty;
    private string _characterPostHistoryInstructions = string.Empty;
    private string _characterPromptStatus =
        "选择个人聊天后可直接查看和修改该角色卡的提示词。";
    private string _activeModelText = "聊天功能尚未分配模型";
    private string _apiRequestPreview = "选择会话后显示本次请求的角色与内容结构；不会显示 API Key。";
    private ChatSendMode _sendMode = ChatSendMode.SendAndGenerate;
    private ChatDisplayMode _displayMode = ChatDisplayMode.Bubble;
    private ModelFunctionAssignment? _chatAssignment;
    private ModelFunctionAssignment? _groupChatAssignment;
    private TokenEstimate _tokenEstimate;
    private bool _disposed;

    public ChatViewModel(
        IConversationRepository repository,
        ICharacterRepository characters,
        IMemoryBankService memoryBanks,
        IMemoryWorkflowRepository memoryWorkflow,
        IMemoryPromptComposer memoryPrompts,
        IGroupChatRepository groupChats,
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
        Func<string, Task>? openConversationWindow = null)
    {
        _repository = repository;
        _characters = characters;
        _groupChats = groupChats;
        _groupRelayPlanner = groupRelayPlanner;
        _contextAssembler = contextAssembler;
        _contextBudget = contextBudget;
        _generationCoordinator = generationCoordinator;
        _generationSessions = generationSessions;
        _modelAssignments = modelAssignments;
        _providerGateway = providerGateway;
        _settings = settings;
        _globalPrompts = globalPrompts;
        _interaction = interaction;
        _chatArchives = chatArchives;
        _fileDialog = fileDialog;
        _openConversationWindow = openConversationWindow;
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
            (character, groupSettings) =>
                Memory.GenerateGroupMergeAsync(character, groupSettings));
        Retrieval = new RetrievalViewModel(retrieval, ScheduleContextRefresh);
        Presets = new PresetViewModel(
            presets,
            presetResolver,
            interaction,
            ScheduleContextRefresh);
        Memory.BodyChanged += (_, _) => ScheduleContextRefresh();
        var budget = _contextBudget.GetCurrentBudget();
        _tokenEstimate = new TokenEstimate(
            0,
            budget.ReservedOutputTokens,
            budget.ContextLimit,
            IsExact: false);

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
        SavePersonaCommand = new AsyncRelayCommand(SavePersonaAsync);
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
        _generationCoordinator.StateChanged += OnGenerationStateChanged;
        _generationSessions.SessionChanged += OnGenerationSessionChanged;
    }

    public ObservableCollection<CharacterConversationGroupViewModel> ConversationGroups { get; } = [];
    public ObservableCollection<ChatMessageItemViewModel> Messages { get; } = [];
    public ObservableCollection<ContextSegment> ContextSegments { get; } = [];
    public MemoryWorkflowViewModel Memory { get; }
    public GroupChatViewModel Group { get; }
    public RetrievalViewModel Retrieval { get; }
    public PresetViewModel Presets { get; }
    public RelayCommand SelectConversationCommand { get; }
    public RelayCommand SendLocalCommand { get; }
    public RelayCommand StopGenerationCommand { get; }
    public AsyncRelayCommand SavePersonaCommand { get; }
    public AsyncRelayCommand EditCharacterSystemPromptCommand { get; }
    public AsyncRelayCommand EditCharacterPostHistoryCommand { get; }
    public AsyncRelayCommand OpenGlobalPromptCommand { get; }
    public AsyncRelayCommand ImportChatArchiveCommand { get; }
    public AsyncRelayCommand ExportChatArchiveCommand { get; }
    public Func<GlobalPromptKey, Task>? OpenPromptSettings { get; set; }
    public IReadOnlyList<ChatSendModeOption> SendModes { get; } =
    [
        new(ChatSendMode.SendAndGenerate, "发送并生成回复"),
        new(ChatSendMode.SaveOnly, "只保存用户消息")
    ];
    public IReadOnlyList<ChatDisplayModeOption> DisplayModes { get; } =
    [
        new(ChatDisplayMode.Bubble, "气泡模式"),
        new(ChatDisplayMode.Novel, "小说模式")
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
                        ? "请选择一次对话。"
                        : "会话已加载；本地数据就绪。";
                OnPropertyChanged(nameof(IsCurrentConversationGenerating));
                OnPropertyChanged(nameof(IsCurrentConversationBusy));
                OnPropertyChanged(nameof(IsModelThinking));
                OnPropertyChanged(nameof(LastGenerationUsageText));
                OnPropertyChanged(nameof(IsSingleCharacterConversation));
                StopGenerationCommand.RaiseCanExecuteChanged();
                ExportChatArchiveCommand.RaiseCanExecuteChanged();
                EditCharacterSystemPromptCommand.RaiseCanExecuteChanged();
                EditCharacterPostHistoryCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsSingleCharacterConversation =>
        SelectedConversation?.Mode == ConversationMode.SingleCharacter;

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

            ScheduleContextRefresh();
            SendLocalCommand.RaiseCanExecuteChanged();
        }
    }

    public string EstimatedTokenText
    {
        get
        {
            var budget = _contextBudget.GetCurrentBudget();
            var accuracy = _tokenEstimate.IsExact ? "精确" : "估算";
            return _tokenEstimate.ExceedsLimit
                ? $"{accuracy}输入 {_tokenEstimate.InputTokens} + 预留输出 {_tokenEstimate.ReservedOutputTokens} tokens，超过上下文 {_tokenEstimate.ContextLimit}（{budget.SourceLabel}）"
                : $"{accuracy}输入 {_tokenEstimate.InputTokens} + 预留输出 {_tokenEstimate.ReservedOutputTokens} tokens（{budget.SourceLabel}）";
        }
    }

    public int EstimatedInputTokens => _tokenEstimate.InputTokens;
    public bool IsEstimatedOverLimit => _tokenEstimate.ExceedsLimit;

    public bool IsModelThinking =>
        SelectedConversation is not null
        && _generationSessions.Get(SelectedConversation.Id).IsThinking;

    public string LastGenerationUsageText
    {
        get
        {
            if (SelectedConversation is null)
            {
                return "选择会话后显示模型返回的实际 Token 用量。";
            }

            var telemetry = _generationSessions.Get(SelectedConversation.Id);
            if (telemetry.OperationId is null)
            {
                return "本会话尚无模型实际 Token 记录。";
            }

            if (telemetry.Usage is null)
            {
                return telemetry.IsBusy
                    ? "本次生成进行中；服务返回用量后显示实际 Token。"
                    : "服务未返回本次生成的实际 Token 用量。";
            }

            var usage = telemetry.Usage;
            var reasoning = usage.ReasoningTokens is > 0
                ? $"，其中思考 {usage.ReasoningTokens}"
                : string.Empty;
            var cache = usage.CachedPromptTokens is { } cached
                ? $"，输入缓存命中 {cached}"
                  + (usage.UncachedPromptTokens is { } uncached
                      ? $" / 未命中 {uncached}"
                      : string.Empty)
                : string.Empty;
            return $"最近实际：输入 {usage.PromptTokens} + 输出 "
                   + $"{usage.CompletionTokens} = {usage.TotalTokens} tokens"
                   + $"{reasoning}{cache}；"
                   + FinishReasonLabel(telemetry.FinishReason);
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

    public string ActiveModelText
    {
        get => _activeModelText;
        private set => SetProperty(ref _activeModelText, value);
    }

    public string ApiRequestPreview
    {
        get => _apiRequestPreview;
        private set => SetProperty(ref _apiRequestPreview, value);
    }

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
        && _generationCoordinator.GetState(SelectedConversation.Id).Status
            is ConversationGenerationStatus.Queued
            or ConversationGenerationStatus.Streaming
            or ConversationGenerationStatus.Stopping;

    public bool IsCurrentConversationBusy =>
        SelectedConversation is not null
        && IsConversationBusy(SelectedConversation.Id);

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
            Status = "正在导入聊天 JSONL…";
            var result = await _chatArchives.ImportAsync(path);
            await ReloadGroupsAsync(result.Conversation.Id);
            var warningText = result.Warnings.Count == 0
                ? string.Empty
                : $"；{result.Warnings.Count} 条兼容提示可在后续导出前检查";
            Status =
                $"已导入 {result.MessageCount} 条消息、{result.CandidateCount} 个候选回复，"
                + $"关联角色“{result.CharacterName}”{warningText}。";
        }
        catch (Exception exception)
        {
            Status = $"聊天 JSONL 导入失败：{exception.Message}";
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
            Status = "正在导出当前聊天 JSONL…";
            var result = await _chatArchives.ExportAsync(selected.Id, path);
            Status =
                $"已导出 {result.MessageCount} 条消息、{result.CandidateCount} 个候选回复"
                + (result.Warnings.Count == 0
                    ? "。"
                    : $"；有 {result.Warnings.Count} 条兼容提示。");
        }
        catch (Exception exception)
        {
            Status = $"聊天 JSONL 导出失败：{exception.Message}";
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
        await _groupReloadGate.WaitAsync();
        try
        {
            await ReloadGroupsCoreAsync(preferredConversationId);
        }
        finally
        {
            _groupReloadGate.Release();
        }
    }

    private async Task ReloadGroupsCoreAsync(string? preferredConversationId)
    {
        var expandedOwners = _allGroups
            .Where(group => group.IsExpanded)
            .Select(group => group.OwnerId)
            .ToHashSet(StringComparer.Ordinal);
        var characterTask = _characters.ListAsync();
        var conversationTask = _repository.ListAllAsync();
        await Task.WhenAll(characterTask, conversationTask);

        _characterLookup.Clear();
        foreach (var character in characterTask.Result)
        {
            _characterLookup[character.Id] = character;
        }
        _allGroups.Clear();

        foreach (var grouping in conversationTask.Result
                     .GroupBy(
                         conversation => conversation.Mode == ConversationMode.Group
                             ? "__group__"
                             : conversation.CharacterId ?? "__deleted__",
                         StringComparer.Ordinal))
        {
            var items = grouping
                .Select(summary => new ConversationListItemViewModel(
                    summary,
                    _generationCoordinator.GetState(summary.Id),
                    _openConversationWindow))
                .ToArray();
            if (items.Length == 0)
            {
                continue;
            }

            CharacterConversationGroupViewModel group;
            if (grouping.Key == "__group__")
            {
                group = new CharacterConversationGroupViewModel(
                    "__group__",
                    "群聊",
                    string.Empty,
                    isGroup: true,
                    items);
            }
            else if (grouping.Key == "__deleted__")
            {
                group = new CharacterConversationGroupViewModel(
                    "__deleted__",
                    "已删除角色",
                    string.Empty,
                    isGroup: false,
                    items);
            }
            else if (_characterLookup.TryGetValue(grouping.Key, out var character))
            {
                group = new CharacterConversationGroupViewModel(
                    character.Id,
                    character.Name,
                    character.AvatarPath,
                    isGroup: false,
                    items);
            }
            else
            {
                group = new CharacterConversationGroupViewModel(
                    grouping.Key,
                    "已删除角色",
                    string.Empty,
                    isGroup: false,
                    items);
            }

            group.IsExpanded = expandedOwners.Contains(group.OwnerId)
                               || group.FindConversation(preferredConversationId ?? string.Empty) is not null;
            _allGroups.Add(group);
        }

        _allGroups.Sort((left, right) =>
        {
            var updatedComparison = right.UpdatedAt.CompareTo(left.UpdatedAt);
            return updatedComparison != 0
                ? updatedComparison
                : string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
        });
        ApplyConversationFilter(preferredConversationId);
    }

    private void ApplyConversationFilter(string? preferredConversationId = null)
    {
        var selectedId = preferredConversationId ?? SelectedConversation?.Id;
        ConversationGroups.Clear();
        foreach (var group in _allGroups.Where(group => group.ApplyFilter(ConversationSearchText)))
        {
            ConversationGroups.Add(group);
        }

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

    private ConversationListItemViewModel? FindConversation(string? conversationId)
    {
        if (conversationId is null)
        {
            return null;
        }

        return _allGroups
            .Select(group => group.FindConversation(conversationId))
            .FirstOrDefault(item => item is not null);
    }

    private void SelectConversation(ConversationListItemViewModel conversation)
    {
        if (ReferenceEquals(SelectedConversation, conversation))
        {
            return;
        }

        if (SelectedConversation is not null)
        {
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
        _selectionCancellation?.Cancel();
        _selectionCancellation?.Dispose();
        _selectionCancellation = null;
        _contextCancellation?.Cancel();
        _contextCancellation?.Dispose();
        _contextCancellation = null;
        if (SelectedConversation is not null)
        {
            SelectedConversation.IsSelected = false;
        }

        SelectedConversation = null;
        Messages.Clear();
        ContextSegments.Clear();
        ApiRequestPreview = "选择会话后显示本次请求结构；API Key 永远不进入此预览。";
        Memory.Clear();
        Group.Clear();
        Retrieval.Clear();
        Presets.Clear();
        ApplyCharacterPrompts(null);
        RefreshTokenEstimate(new TokenEstimate(
            0,
            _contextBudget.GetCurrentBudget().ReservedOutputTokens,
            _contextBudget.GetCurrentBudget().ContextLimit,
            IsExact: false));
        SendLocalCommand.RaiseCanExecuteChanged();
    }

    private void StartSelectionLoad(ConversationListItemViewModel conversation)
    {
        _selectionCancellation?.Cancel();
        _selectionCancellation?.Dispose();
        _selectionCancellation = new CancellationTokenSource();
        var version = ++_selectionVersion;
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
            var messagesTask = _repository.ListMessagesAsync(conversation.Id, cancellationToken);
            var conversationTask = _repository.GetAsync(conversation.Id, cancellationToken);
            await Task.WhenAll(messagesTask, conversationTask);
            var loadedConversation = conversationTask.Result
                ?? throw new InvalidOperationException("所选会话已经不存在。");

            if (cancellationToken.IsCancellationRequested
                || version != _selectionVersion
                || SelectedConversation?.Id != conversation.Id)
            {
                return;
            }

            Messages.Clear();
            foreach (var message in messagesTask.Result)
            {
                Messages.Add(CreateMessageItem(message));
            }
            ApplyLiveSession(_generationSessions.Get(conversation.Id));

            var ownerId = loadedConversation.Mode == ConversationMode.Group
                ? MemoryOwnerIds.ForGroup(loadedConversation.Id)
                : loadedConversation.CharacterId
                  ?? throw new InvalidOperationException("单角色会话缺少角色引用。");
            var ownerLabel = loadedConversation.Mode == ConversationMode.Group
                ? $"群聊独立记忆 · {loadedConversation.Title}"
                : $"角色共享记忆 · {_characterLookup.GetValueOrDefault(ownerId)?.Name ?? loadedConversation.Title}";
            Character? promptCharacter = null;
            if (loadedConversation.Mode == ConversationMode.SingleCharacter)
            {
                promptCharacter = await _characters.GetAsync(
                    ownerId,
                    cancellationToken)
                    ?? throw new InvalidOperationException("所选角色已经不存在。");
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
                    cancellationToken),
                Group.LoadAsync(loadedConversation, cancellationToken),
                Retrieval.LoadAsync(loadedConversation, cancellationToken),
                Presets.LoadAsync(loadedConversation, cancellationToken));
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
                Status = $"读取会话失败：{exception.Message}";
            }
        }
    }

    private bool CanSendLocal() =>
        SelectedConversation is not null
        && !string.IsNullOrWhiteSpace(ComposerText)
        && !IsEstimatedOverLimit
        && !IsCurrentConversationBusy
        && (SendMode == ChatSendMode.SaveOnly
            || AssignmentFor(SelectedConversation.Mode) is not null);

    private void StartSend()
    {
        var selected = SelectedConversation;
        if (selected is null
            || !_generationSessions.TryBegin(
                selected.Id,
                out var operationId))
        {
            return;
        }

        var budget = _contextBudget.GetCurrentBudget();
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
        if (selected?.Mode != ConversationMode.Group)
        {
            Status = "当前不是群聊。";
            return;
        }

        if (!_generationSessions.TryBegin(
                selected.Id,
                out var operationId))
        {
            Status = "当前群聊已有生成任务。";
            return;
        }

        RaiseCurrentConversationBusyChanged(selected.Id);
        try
        {
            var assignment = _groupChatAssignment;
            if (assignment is null)
            {
                Status = "“群聊接力”尚未分配模型。";
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
                    _contextBudget.GetCurrentBudget(),
                    manualSpeakerId),
                operationId);
            var messages = await _repository.ListMessagesAsync(selected.Id);
            var decision = DecideGroupNext(snapshot.Context, messages);
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
                    decision.Reason);
                SetStatusForConversation(selected.Id, decision.Reason);
                return;
            }

            var context = await AssembleContextAsync(
                selected.Id,
                userInput: string.Empty,
                historyBeforeSequenceNo: null,
                snapshot: snapshot.Context with
                {
                    SpeakerCharacterId = decision.NextSpeakerId
                });
            if (context.Estimate.ExceedsLimit)
            {
                SetStatusForConversation(
                    selected.Id,
                    "群聊继续所需上下文超过当前模型上限。");
                return;
            }

            var assistant = await GenerateReplyAsync(
                snapshot,
                assignment,
                context,
                decision.NextSpeakerId);
            if (assistant is null)
            {
                return;
            }

            await ReloadGroupsPreservingSelectionAsync();
            TriggerAutoMemory(snapshot);
            if (_generationCoordinator.GetState(selected.Id).Status
                != ConversationGenerationStatus.Interrupted)
            {
                await ContinueGroupRelayAsync(snapshot, assistant);
            }
        }
        catch (Exception exception)
        {
            SetStatusForConversation(
                selected.Id,
                $"群聊接力失败：{exception.Message}");
        }
        finally
        {
            _generationSessions.End(selected.Id, operationId);
            RaiseCurrentConversationBusyChanged(selected.Id);
            SendLocalCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task SendAsync(SendSnapshot snapshot)
    {
        var conversationId = snapshot.ConversationId;
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
                var messages = (await _repository.ListMessagesAsync(conversationId))
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
                    SetStatusForConversation(conversationId, decision.Reason);
                    return;
                }

                speakerId = decision.NextSpeakerId;
            }

            var context = await AssembleContextAsync(
                conversationId,
                snapshot.Input,
                historyBeforeSequenceNo: null,
                snapshot: snapshot.Context with { SpeakerCharacterId = speakerId });
            if (context.Estimate.ExceedsLimit)
            {
                SetStatusForConversation(
                    conversationId,
                    "预计上下文超过当前模型上限，消息未发送，也没有自动截断内容。");
                return;
            }

            var message = new ChatMessage
            {
                ConversationId = conversationId,
                SenderKind = MessageSenderKind.User,
                SenderId = "local-user",
                Content = snapshot.Input
            };
            await _repository.AddMessageAsync(message);
            if (SelectedConversation?.Id == conversationId
                && string.Equals(
                    ComposerText.Trim(),
                    snapshot.Input,
                    StringComparison.Ordinal))
            {
                ComposerText = string.Empty;
            }

            await ReloadGroupsPreservingSelectionAsync();
            if (snapshot.SendMode == ChatSendMode.SaveOnly)
            {
                SetStatusForConversation(
                    conversationId,
                    "用户消息已保存；按当前模式未调用模型。");
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
                return;
            }

            await ReloadGroupsPreservingSelectionAsync();
            TriggerAutoMemory(snapshot);
            if (snapshot.Mode == ConversationMode.Group
                && _generationCoordinator.GetState(conversationId).Status
                    != ConversationGenerationStatus.Interrupted)
            {
                await ContinueGroupRelayAsync(snapshot, assistant);
            }
        }
        catch (Exception exception)
        {
            SetStatusForConversation(
                conversationId,
                $"发送或生成失败：{exception.Message}");
            await ReloadGroupsPreservingSelectionAsync();
        }
        finally
        {
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
        var assistant = new ChatMessage
        {
            ConversationId = snapshot.ConversationId,
            SenderKind = MessageSenderKind.Character,
            SenderId = speakerId,
            Content = string.Empty,
            ActiveCandidateIndex = 0
        };
        _generationSessions.BeginReply(
            snapshot.ConversationId,
            snapshot.OperationId,
            assistant.Id,
            speakerId,
            LiveReplyKind.NewMessage);
        BeginProviderGeneration(snapshot.ConversationId);
        var buffer = new System.Text.StringBuilder();
        await _generationCoordinator.RunAsync(
            snapshot.ConversationId,
            token => StreamProviderContentAsync(
                snapshot.ConversationId,
                snapshot.OperationId,
                CreateExecutionRequest(
                    assignment,
                    context,
                    snapshot.ConversationId),
                token),
            (chunk, _) =>
            {
                buffer.Append(chunk);
                assistant.Content = buffer.ToString();
                return ValueTask.CompletedTask;
            });
        var telemetry = _generationSessions.Get(snapshot.ConversationId);

        if (buffer.Length == 0)
        {
            SetStatusForConversation(
                snapshot.ConversationId,
                EmptyReplyStatus(snapshot.ConversationId, telemetry));
            return null;
        }

        assistant.Content = buffer.ToString();
        await _repository.AddMessageAsync(assistant);
        await _repository.AddCandidateAsync(new MessageCandidate
        {
            MessageId = assistant.Id,
            CandidateIndex = 0,
            Content = assistant.Content
        });
        SetStatusForConversation(
            snapshot.ConversationId,
            CompletedReplyStatus(
                snapshot.ConversationId,
                assignment.ModelId,
                telemetry));
        return assistant;
    }

    private async Task ContinueGroupRelayAsync(
        SendSnapshot snapshot,
        ChatMessage currentSpeakerMessage)
    {
        var group = snapshot.Context.Group
                    ?? throw new InvalidOperationException("群聊上下文快照不存在。");
        var automaticTurns = 0;
        var current = currentSpeakerMessage;
        while (true)
        {
            var messages = await _repository.ListMessagesAsync(snapshot.ConversationId);
            var decision = DecideGroupNext(snapshot.Context, messages);
            var shouldPause = decision.PauseForUser || decision.NextSpeakerId is null;
            await SaveGroupStateAsync(
                snapshot.ConversationId,
                current.SenderId,
                decision.NextSpeakerId ?? string.Empty,
                automaticTurns,
                shouldPause,
                decision.Reason);
            if (shouldPause)
            {
                SetStatusForConversation(snapshot.ConversationId, decision.Reason);
                return;
            }

            if (!group.Settings.AutoContinueEnabled)
            {
                SetStatusForConversation(
                    snapshot.ConversationId,
                    $"{decision.Reason} 自动接力未开启，可点击“继续群聊”。");
                return;
            }

            if (automaticTurns >= group.Settings.MaximumAutomaticTurns)
            {
                const string reason = "已达到本轮自动接力次数上限，等待用户决定是否继续。";
                await SaveGroupStateAsync(
                    snapshot.ConversationId,
                    current.SenderId,
                    decision.NextSpeakerId!,
                    automaticTurns,
                    isPaused: true,
                    reason);
                SetStatusForConversation(snapshot.ConversationId, reason);
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
                });
            if (context.Estimate.ExceedsLimit)
            {
                const string reason = "下一轮群聊上下文超过模型上限，自动接力已暂停。";
                await SaveGroupStateAsync(
                    snapshot.ConversationId,
                    current.SenderId,
                    decision.NextSpeakerId!,
                    automaticTurns,
                    isPaused: true,
                    reason);
                SetStatusForConversation(snapshot.ConversationId, reason);
                return;
            }

            var next = await GenerateReplyAsync(
                snapshot,
                snapshot.Assignment!,
                context,
                decision.NextSpeakerId!);
            if (next is null
                || _generationCoordinator.GetState(snapshot.ConversationId).Status
                    == ConversationGenerationStatus.Interrupted)
            {
                return;
            }

            current = next;
            await ReloadGroupsPreservingSelectionAsync();
            TriggerAutoMemory(snapshot);
        }
    }

    private GroupRelayDecision DecideGroupNext(
        ContextInputSnapshot context,
        IReadOnlyList<ChatMessage> messages)
    {
        var group = context.Group
                    ?? throw new InvalidOperationException("当前没有群聊设置快照。");
        return _groupRelayPlanner.DecideNext(
            group.Settings,
            group.Members,
            group.MemberNames,
            messages,
            context.PersonaName,
            group.ManualSpeakerId);
    }

    private async Task SaveGroupStateAsync(
        string conversationId,
        string currentSpeakerId,
        string nextSpeakerId,
        int automaticTurns,
        bool isPaused,
        string reason)
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
        await _groupChats.SaveStateAsync(state);
        Group.ApplyState(state);
    }

    private void TriggerAutoMemory(SendSnapshot snapshot)
    {
        var ownerId = snapshot.Mode == ConversationMode.Group
            ? MemoryOwnerIds.ForGroup(snapshot.ConversationId)
            : snapshot.CharacterId;
        if (ownerId is not null)
        {
            _ = Memory.TryAutoGenerateAsync(ownerId, snapshot.ConversationId);
        }
    }

    private ChatMessageItemViewModel CreateMessageItem(ChatMessage message) =>
        new(
            message,
            EditMessageAsync,
            DeleteMessageAsync,
            ForkMessageAsync,
            RegenerateMessageAsync,
            CopyMessage,
            OpenMessageTools,
            message.SenderKind == MessageSenderKind.Character
                ? _characterLookup.GetValueOrDefault(message.SenderId)?.Name
                : null);

    private void OpenMessageTools(ChatMessageItemViewModel selected)
    {
        foreach (var item in Messages.Where(item => item.Id != selected.Id))
        {
            item.CloseTools();
        }
    }

    private async Task EditMessageAsync(ChatMessageItemViewModel item)
    {
        item.CloseTools();
        var edited = await _interaction.EditTextAsync(
            "编辑消息",
            "修改当前消息不会截断或重写后续对话。",
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
        Status = "消息已原位修改；后续消息保持不变。";
    }

    private async Task DeleteMessageAsync(ChatMessageItemViewModel item)
    {
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
        Status = decision == DeleteMessageDecision.SelectedAndFollowing
            ? "当前消息及后续消息已永久删除。"
            : "当前消息已永久删除。";
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
        Status = "已复制到完全独立的新聊天；两条记录不会互相跳转。";
    }

    private async Task RegenerateMessageAsync(ChatMessageItemViewModel item)
    {
        item.CloseTools();
        if (SelectedConversation is null
            || item.SenderKind != MessageSenderKind.Character)
        {
            return;
        }

        var conversationId = SelectedConversation.Id;
        var assignment = AssignmentFor(SelectedConversation.Mode);
        if (assignment is null)
        {
            Status = SelectedConversation.Mode == ConversationMode.Group
                ? "群聊接力功能尚未分配模型，不能重新生成。"
                : "角色聊天功能尚未分配模型，不能重新生成。";
            return;
        }

        if (!_generationSessions.TryBegin(
                conversationId,
                out var operationId))
        {
            Status = "当前会话已有生成任务；同一会话不会并发覆盖候选。";
            return;
        }

        var original = item.Message.Content;
        var contextSnapshot = CreateContextSnapshot(_contextBudget.GetCurrentBudget())
            with { SpeakerCharacterId = item.Message.SenderId };
        try
        {
            var context = await AssembleContextAsync(
                conversationId,
                userInput: string.Empty,
                historyBeforeSequenceNo: item.Message.SequenceNo,
                snapshot: contextSnapshot);
            if (context.Estimate.ExceedsLimit)
            {
                Status = "重新生成所需上下文超过模型上限，未调用模型。";
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
                token => StreamProviderContentAsync(
                    conversationId,
                    operationId,
                    CreateExecutionRequest(
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
                });
            var telemetry = _generationSessions.Get(conversationId);

            if (buffer.Length == 0)
            {
                item.Message.Content = original;
                item.RefreshContent();
                SetStatusForConversation(
                    conversationId,
                    EmptyReplyStatus(conversationId, telemetry, isCandidate: true));
                return;
            }

            var candidates = await _repository.ListCandidatesAsync(item.Id);
            var nextIndex = candidates.Count == 0
                ? Math.Max(1, item.Message.ActiveCandidateIndex + 1)
                : candidates.Max(candidate => candidate.CandidateIndex) + 1;
            await _repository.AddAndActivateCandidateAsync(new MessageCandidate
            {
                MessageId = item.Id,
                CandidateIndex = nextIndex,
                Content = buffer.ToString()
            });
            item.Message.Content = buffer.ToString();
            item.Message.ActiveCandidateIndex = nextIndex;
            item.RefreshContent();
            await ReloadGroupsPreservingSelectionAsync();
            var generationInterrupted =
                _generationCoordinator.GetState(conversationId).Status
                == ConversationGenerationStatus.Interrupted;
            var suffix = string.Equals(
                telemetry.FinishReason,
                "length",
                StringComparison.OrdinalIgnoreCase)
                ? "；输出达到上限，候选可能未完成"
                : string.Empty;
            SetStatusForConversation(
                conversationId,
                generationInterrupted
                    ? $"生成已中断；已把部分正文保存为候选 {nextIndex + 1}，后续消息未截断。"
                    : $"已生成并切换到候选 {nextIndex + 1}；后续消息未截断{suffix}。");
        }
        catch (Exception exception)
        {
            item.Message.Content = original;
            item.RefreshContent();
            SetStatusForConversation(
                conversationId,
                $"重新生成失败：{exception.Message}");
        }
        finally
        {
            _generationSessions.End(conversationId, operationId);
            RaiseCurrentConversationBusyChanged(conversationId);
            SendLocalCommand.RaiseCanExecuteChanged();
        }
    }

    private void CopyMessage(ChatMessageItemViewModel item)
    {
        item.CloseTools();
        _interaction.CopyText(item.Content);
        Status = "消息正文已复制。";
    }

    private void StopCurrentGeneration()
    {
        if (SelectedConversation is not null
            && _generationCoordinator.Cancel(SelectedConversation.Id))
        {
            Status = "正在中断当前会话的生成；其他会话的流不会受影响。";
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

        PersonaName = await _settings.GetAsync("persona.name") ?? "USER";
        PersonaDescription = await _settings.GetAsync("persona.description") ?? string.Empty;
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
            Status = $"保存聊天显示模式失败：{exception.Message}";
        }
    }

    private async Task SavePersonaAsync()
    {
        var name = PersonaName.Trim();
        if (name.Length == 0)
        {
            PersonaStatus = "Persona 名称不能为空。";
            return;
        }

        if (name.Length > 80)
        {
            PersonaStatus = "Persona 名称不能超过 80 个字符。";
            return;
        }

        PersonaName = name;
        await _settings.SetAsync("persona.name", PersonaName);
        await _settings.SetAsync("persona.description", PersonaDescription);
        PersonaStatus = $"已保存 Persona“{PersonaName}”；全局提示词请在设置中统一修改。";
        await RefreshContextEstimateAsync(immediate: true);
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
                CharacterPromptStatus = "角色卡已不存在，无法保存局部提示词。";
                return;
            }

            var buffer = new CharacterEditBuffer();
            buffer.Load(character);
            var currentText = editPostHistory
                ? buffer.PostHistoryInstructions
                : buffer.SystemPrompt;
            var edited = await _interaction.EditTextAsync(
                editPostHistory
                    ? $"编辑“{character.Name}”的历史后指令"
                    : $"编辑“{character.Name}”的角色 System Prompt",
                editPostHistory
                    ? "该内容会在聊天历史之后注入，用于强调当前角色每次回复前必须遵守的要求；留空表示不追加。"
                    : "该内容属于角色卡本身，用于补充该角色的专属扮演职责；全局聊天提示词仍会先行生效。留空表示仅使用全局职责与角色卡其他字段。",
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
            _characterLookup[character.Id] = character;

            if (SelectedConversation?.Id == conversationId)
            {
                ApplyCharacterPrompts(character);
                CharacterPromptStatus = editPostHistory
                    ? "已保存角色卡的历史后指令；该角色的下一次请求开始生效。"
                    : "已保存角色卡的 System Prompt；该角色的下一次请求开始生效。";
                await RefreshContextEstimateAsync(immediate: true);
            }
        }
        catch (Exception exception)
        {
            CharacterPromptStatus = $"保存角色局部提示词失败：{exception.Message}";
        }
    }

    private void ApplyCharacterPrompts(Character? character)
    {
        if (character is null)
        {
            _characterPromptCharacterId = string.Empty;
            CharacterPromptCharacterName = "未选择角色";
            CharacterSystemPrompt = string.Empty;
            CharacterPostHistoryInstructions = string.Empty;
            CharacterPromptStatus =
                "选择个人聊天后可直接查看和修改该角色卡的提示词。";
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
                    ? "该角色尚未填写局部提示词；当前由全局聊天提示词、角色描述和聊天历史共同指导。"
                    : "以下内容直接来自角色卡，并会用于该角色的所有个人聊天。";
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
            Status = "当前窗口不能打开全局提示词设置。";
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

    private void ApplyActiveAssignmentBudget(ConversationMode mode)
    {
        var assignment = AssignmentFor(mode);
        var functionName = mode == ConversationMode.Group ? "群聊接力" : "角色聊天";
        if (assignment is null)
        {
            _contextBudget.UpdateBudget(new ContextBudget(
                32768,
                4096,
                $"{functionName}尚未分配模型"));
            ActiveModelText =
                $"{functionName}尚未分配模型；可切换为“只保存用户消息”。";
        }
        else
        {
            _contextBudget.UpdateBudget(new ContextBudget(
                assignment.ContextLimit,
                assignment.MaxOutputTokens,
                $"{assignment.ProviderId} / {assignment.ModelId}",
                assignment.ModelId));
            ActiveModelText =
                $"{functionName} · {assignment.ModelId} · 上下文 {assignment.ContextLimit} · 输出 {assignment.MaxOutputTokens}";
        }

        OnPropertyChanged(nameof(EstimatedTokenText));
    }

    private void ScheduleContextRefresh()
    {
        _contextCancellation?.Cancel();
        _contextCancellation?.Dispose();
        _contextCancellation = new CancellationTokenSource();
        var version = ++_contextVersion;
        _contextRefreshTask = RefreshContextEstimateAsync(
            immediate: false,
            version,
            _contextCancellation.Token);
    }

    private async Task RefreshContextEstimateAsync(
        bool immediate,
        long? requestedVersion = null,
        CancellationToken cancellationToken = default)
    {
        var selected = SelectedConversation;
        if (selected is null)
        {
            return;
        }

        var version = requestedVersion ?? ++_contextVersion;
        try
        {
            if (!immediate)
            {
                await Task.Delay(150, cancellationToken);
            }

            var result = await AssembleContextAsync(
                selected.Id,
                ComposerText,
                historyBeforeSequenceNo: null,
                cancellationToken);
            if (cancellationToken.IsCancellationRequested
                || version != _contextVersion
                || SelectedConversation?.Id != selected.Id)
            {
                return;
            }

            ContextSegments.Clear();
            foreach (var segment in result.Segments)
            {
                ContextSegments.Add(segment);
            }

            ApiRequestPreview = RenderApiRequestPreview(result);
            RefreshTokenEstimate(result.Estimate);
            Retrieval.UpdateFromContext(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (version == _contextVersion)
            {
                Status = $"上下文估算失败：{exception.Message}";
            }
        }
    }

    private Task<ContextAssemblyResult> AssembleContextAsync(
        string conversationId,
        string userInput,
        long? historyBeforeSequenceNo,
        CancellationToken cancellationToken = default)
    {
        return AssembleContextAsync(
            conversationId,
            userInput,
            historyBeforeSequenceNo,
            CreateContextSnapshot(_contextBudget.GetCurrentBudget()),
            cancellationToken);
    }

    private Task<ContextAssemblyResult> AssembleContextAsync(
        string conversationId,
        string userInput,
        long? historyBeforeSequenceNo,
        ContextInputSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        return _contextAssembler.AssembleAsync(
            new ContextAssemblyRequest(
                conversationId,
                userInput,
                snapshot.ContextLimit,
                snapshot.ReservedOutputTokens,
                MemoryOverride: snapshot.MemoryBody,
                PersonaName: snapshot.PersonaName,
                PersonaDescription: snapshot.PersonaDescription,
                GlobalPreset: snapshot.GlobalPreset,
                HistoryBeforeSequenceNo: historyBeforeSequenceNo,
                SpeakerCharacterId: snapshot.SpeakerCharacterId,
                GroupMemberIds: snapshot.Group?.Members
                    .Where(member => member.IsEnabled)
                    .Select(member => member.CharacterId)
                    .ToArray(),
                GroupMemoryOverride: snapshot.Group is null
                    ? null
                    : snapshot.MemoryBody,
                GroupSystemPrompt: snapshot.Group?.Settings.GroupSystemPrompt,
                GroupBatonInstruction: BuildGroupBatonInstruction(snapshot),
                Retrieval: snapshot.Retrieval,
                ModelId: snapshot.ModelId),
            cancellationToken);
    }

    private ContextInputSnapshot CreateContextSnapshot(
        ContextBudget budget,
        string? manualSpeakerId = null)
    {
        GroupContextSnapshot? group = null;
        if (SelectedConversation?.Mode == ConversationMode.Group
            && Group.IsGroupConversation)
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
            Memory.Body,
            PersonaName,
            PersonaDescription,
            Presets.EffectiveSystemPrompt(
                _globalPrompts.Get(GlobalPromptKey.ChatSystem)),
            SpeakerCharacterId: group?.ManualSpeakerId,
            Group: group,
            Retrieval: Retrieval.Snapshot());
    }

    private static string? BuildGroupBatonInstruction(ContextInputSnapshot snapshot)
    {
        if (snapshot.Group is null || snapshot.SpeakerCharacterId is null)
        {
            return null;
        }

        var speaker = snapshot.Group.MemberNames.GetValueOrDefault(
            snapshot.SpeakerCharacterId,
            snapshot.SpeakerCharacterId);
        var enabledNames = string.Join(
            "、",
            snapshot.Group.Members
                .Where(member => member.IsEnabled)
                .Select(member => snapshot.Group.MemberNames.GetValueOrDefault(
                    member.CharacterId,
                    member.CharacterId)));
        return
            $"本轮只以“{speaker}”身份回复。可接力成员：{enabledNames}。"
            + $"需要用户时在最后一句 @{snapshot.PersonaName} 或 @USER；"
            + "需要角色接力时在最后一句 @下一位角色名。";
    }

    private async Task ReloadGroupsPreservingSelectionAsync()
    {
        await _groupReloadGate.WaitAsync();
        try
        {
            await ReloadGroupsCoreAsync(SelectedConversation?.Id);
        }
        finally
        {
            _groupReloadGate.Release();
        }
    }

    private static ModelExecutionRequest CreateExecutionRequest(
        ModelFunctionAssignment assignment,
        ContextAssemblyResult context,
        string conversationId) =>
        new(
            assignment.ProviderId,
            assignment.ModelId,
            context.Segments
                .Select(segment => new ProviderChatMessage(
                    segment.ProviderRole,
                    segment.ProviderContent ?? segment.Content))
                .ToArray(),
            assignment.MaxOutputTokens,
            assignment.Temperature,
            assignment.TopP,
            assignment.ReasoningEnabled,
            SessionId: $"chat:{conversationId}");

    private static string RenderApiRequestPreview(ContextAssemblyResult context)
    {
        var payload = new
        {
            messages = context.Segments.Select(segment => new
            {
                role = segment.ProviderRole,
                source = segment.Title,
                content = segment.ProviderContent ?? segment.Content
            }),
            token_estimate = new
            {
                input = context.Estimate.InputTokens,
                reserved_output = context.Estimate.ReservedOutputTokens,
                context_limit = context.Estimate.ContextLimit,
                is_exact = context.Estimate.IsExact
            }
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    private void BeginProviderGeneration(string conversationId)
    {
        SetStatusForConversation(conversationId, "正在等待模型响应…");
    }

    // Reasoning is deliberately reduced to a UI-only signal. Only Content events
    // enter the coordinator, message bubbles, candidates, and persistent storage.
    private async IAsyncEnumerable<string> StreamProviderContentAsync(
        string conversationId,
        string operationId,
        ModelExecutionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var streamEvent in _providerGateway.StreamChatAsync(
                           request,
                           cancellationToken).WithCancellation(cancellationToken))
        {
            _generationSessions.ApplyProviderEvent(
                conversationId,
                operationId,
                streamEvent);
            switch (streamEvent.Kind)
            {
                case ProviderStreamEventKind.Reasoning:
                    SetStatusForConversation(
                        conversationId,
                        "模型正在思考；思考过程不会写入聊天记录。");
                    break;

                case ProviderStreamEventKind.Content:
                    if (streamEvent.Content.Length == 0)
                    {
                        break;
                    }

                    SetStatusForConversation(conversationId, "正在接收模型正文…");
                    yield return streamEvent.Content;
                    break;

                case ProviderStreamEventKind.Completed:
                    break;
            }
        }
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
                ? "生成已停止；思考过程未保存，也没有产生正文。"
                : "生成已停止，没有产生可保存正文。";
        }

        if (string.Equals(
                telemetry.FinishReason,
                "length",
                StringComparison.OrdinalIgnoreCase)
            && telemetry.SawReasoning)
        {
            return "输出上限在思考阶段耗尽，未生成可保存正文；请提高输出上限。";
        }

        return isCandidate
            ? "模型没有返回新的候选正文。"
            : "模型没有返回可保存的正文。";
    }

    private string CompletedReplyStatus(
        string conversationId,
        string modelId,
        ConversationGenerationSession telemetry)
    {
        if (_generationCoordinator.GetState(conversationId).Status
            == ConversationGenerationStatus.Interrupted)
        {
            return "生成已中断；已保存收到的部分回复。";
        }

        return string.Equals(
            telemetry.FinishReason,
            "length",
            StringComparison.OrdinalIgnoreCase)
            ? "已保存收到的正文；输出达到上限，回复可能未完成。"
            : $"已由 {modelId} 完成回复。";
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
            "stop" => "正常结束",
            "length" => "达到输出上限",
            "content_filter" => "内容过滤终止",
            "tool_calls" => "工具调用终止",
            null or "" => "服务未报告完成原因",
            _ => $"完成原因 {finishReason}"
        };

    private void RefreshTokenEstimate(TokenEstimate estimate)
    {
        _tokenEstimate = estimate;
        OnPropertyChanged(nameof(EstimatedInputTokens));
        OnPropertyChanged(nameof(EstimatedTokenText));
        OnPropertyChanged(nameof(IsEstimatedOverLimit));
        SendLocalCommand.RaiseCanExecuteChanged();
    }

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

    private void OnGenerationSessionChanged(
        object? sender,
        ConversationGenerationSession session)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => ApplyGenerationSession(session));
            return;
        }

        ApplyGenerationSession(session);
    }

    private void ApplyGenerationSession(ConversationGenerationSession session)
    {
        if (SelectedConversation?.Id != session.ConversationId)
        {
            return;
        }

        OnPropertyChanged(nameof(IsModelThinking));
        OnPropertyChanged(nameof(LastGenerationUsageText));
        OnPropertyChanged(nameof(IsCurrentConversationBusy));
        SendLocalCommand.RaiseCanExecuteChanged();
        ApplyLiveSession(session);
        if (session.IsThinking)
        {
            Status = "模型正在思考；思考过程不会写入聊天记录。";
        }
        else if (session.IsBusy && session.SawContent)
        {
            Status = "正在接收模型正文…";
        }

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
            return;
        }

        item.Message.Content = session.PartialContent;
        item.RefreshContent();
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
                $"刷新已完成回复失败：{exception.Message}");
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
            StopGenerationCommand.RaiseCanExecuteChanged();
            SendLocalCommand.RaiseCanExecuteChanged();
        }
    }

    private void RaiseCurrentConversationBusyChanged(string conversationId)
    {
        if (SelectedConversation?.Id == conversationId)
        {
            OnPropertyChanged(nameof(IsCurrentConversationBusy));
        }
    }

    public void Dispose() => BeginDispose();

    public async ValueTask DisposeAsync()
    {
        BeginDispose();
        await Task.WhenAll(_selectionLoadTask, _contextRefreshTask);
    }

    private void BeginDispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _generationCoordinator.StateChanged -= OnGenerationStateChanged;
        _generationSessions.SessionChanged -= OnGenerationSessionChanged;
        _selectionCancellation?.Cancel();
        _selectionCancellation?.Dispose();
        _contextCancellation?.Cancel();
        _contextCancellation?.Dispose();
    }

    private sealed record ContextInputSnapshot(
        int ContextLimit,
        int ReservedOutputTokens,
        string? ModelId,
        string MemoryBody,
        string PersonaName,
        string PersonaDescription,
        string GlobalPreset,
        string? SpeakerCharacterId,
        GroupContextSnapshot? Group,
        RetrievalContextOptions? Retrieval);

    private sealed record GroupContextSnapshot(
        GroupChatSettings Settings,
        IReadOnlyList<GroupChatMember> Members,
        IReadOnlyDictionary<string, string> MemberNames,
        string? ManualSpeakerId);

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
