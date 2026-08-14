using System.Text;
using System.Text.Json;
using TavernDesk.App.Localization;
using TavernDesk.App.Presentation;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;

namespace TavernDesk.App.ViewModels;

public sealed class MemoryWorkflowViewModel : ViewModelBase
{
    private readonly IMemoryBankService _memoryBanks;
    private readonly IMemoryWorkflowRepository _workflow;
    private readonly IMemoryPromptComposer _prompts;
    private readonly IConversationRepository _conversations;
    private readonly ICharacterRepository _characters;
    private readonly IModelAssignmentRepository _assignments;
    private readonly IProviderGateway _gateway;
    private readonly IConversationGenerationCoordinator _generationCoordinator;
    private readonly IGlobalPromptConfiguration _globalPrompts;
    private readonly SemaphoreSlim _generationGate = new(1, 1);
    private CancellationTokenSource? _generationCancellation;
    private string? _ownerId;
    private string? _conversationId;
    private string _ownerLabel = LanguageRuntime.GetString("Memory.OwnerNone");
    private string _userIdentity = "用户";
    private string _body = string.Empty;
    private string _bodyBaseline = string.Empty;
    private bool _suppressBodyDirty;
    private bool _isBodyDirty;
    private string _targetTokens = "5000";
    private bool _autoGenerateEnabled = true;
    private string _updateIntervalTurns = "20";
    private string _maximumSourceUserTurns = "20";
    private bool _sendOnlyNewMessages = true;
    private string _status = LanguageRuntime.GetString("Memory.SelectConversation");
    private string _checkpointText = LanguageRuntime.GetString("Memory.CheckpointNone");
    private string _requestPreview = LanguageRuntime.GetString("Memory.RequestPreviewHint");
    private string _pendingBody = string.Empty;
    private string _pendingTargetText = LanguageRuntime.GetString("Memory.NoPendingDraft");
    private MemoryUpdateDraft? _pendingDraft;
    private long _loadVersion;
    private long _loadedBankRevision;
    private bool _isGenerating;

    public MemoryWorkflowViewModel(
        IMemoryBankService memoryBanks,
        IMemoryWorkflowRepository workflow,
        IMemoryPromptComposer prompts,
        IConversationRepository conversations,
        ICharacterRepository characters,
        IModelAssignmentRepository assignments,
        IProviderGateway gateway,
        IConversationGenerationCoordinator generationCoordinator,
        IGlobalPromptConfiguration globalPrompts)
    {
        _memoryBanks = memoryBanks;
        _workflow = workflow;
        _prompts = prompts;
        _conversations = conversations;
        _characters = characters;
        _assignments = assignments;
        _gateway = gateway;
        _generationCoordinator = generationCoordinator;
        _globalPrompts = globalPrompts;

        SaveBodyCommand = new AsyncRelayCommand(SaveBodyAsync, () => IsLoaded);
        SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync, () => IsLoaded);
        PreviewUpdateCommand = new AsyncRelayCommand(PreviewUpdateAsync, () => IsLoaded);
        GenerateUpdateCommand = new AsyncRelayCommand(
            GenerateUpdateAsync,
            () => IsLoaded && !IsGenerating);
        GenerateCompressionCommand = new AsyncRelayCommand(
            GenerateCompressionAsync,
            () => IsLoaded && !IsGenerating);
        SaveDraftCommand = new AsyncRelayCommand(
            SaveDraftAsync,
            () => _pendingDraft is not null);
        DiscardDraftCommand = new AsyncRelayCommand(
            DiscardDraftAsync,
            () => _pendingDraft is not null);
        StopGenerationCommand = new RelayCommand(
            StopGeneration,
            () => IsGenerating);
    }

    public event EventHandler? BodyChanged;
    public event EventHandler<MemoryBodySavedEventArgs>? BodySaved;

    public AsyncRelayCommand SaveBodyCommand { get; }
    public AsyncRelayCommand SaveSettingsCommand { get; }
    public AsyncRelayCommand PreviewUpdateCommand { get; }
    public AsyncRelayCommand GenerateUpdateCommand { get; }
    public AsyncRelayCommand GenerateCompressionCommand { get; }
    public AsyncRelayCommand SaveDraftCommand { get; }
    public AsyncRelayCommand DiscardDraftCommand { get; }
    public RelayCommand StopGenerationCommand { get; }

    public bool IsLoaded => _ownerId is not null && _conversationId is not null;
    public string? OwnerId => _ownerId;
    public string? ConversationId => _conversationId;

    public void SetUserIdentity(string? userIdentity) =>
        _userIdentity = NormalizeUserIdentity(userIdentity);

    public string OwnerLabel
    {
        get => _ownerLabel;
        private set => SetProperty(ref _ownerLabel, value);
    }

    public string Body
    {
        get => _body;
        set
        {
            if (SetProperty(ref _body, value))
            {
                if (!_suppressBodyDirty && IsLoaded)
                {
                    IsBodyDirty = !string.Equals(
                        _body,
                        _bodyBaseline,
                        StringComparison.Ordinal);
                }

                BodyChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool IsBodyDirty
    {
        get => _isBodyDirty;
        private set => SetProperty(ref _isBodyDirty, value);
    }

    public string TargetTokens
    {
        get => _targetTokens;
        set => SetProperty(ref _targetTokens, value);
    }

    public bool AutoGenerateEnabled
    {
        get => _autoGenerateEnabled;
        set => SetProperty(ref _autoGenerateEnabled, value);
    }

    public string UpdateIntervalTurns
    {
        get => _updateIntervalTurns;
        set => SetProperty(ref _updateIntervalTurns, value);
    }

    public string MaximumSourceUserTurns
    {
        get => _maximumSourceUserTurns;
        set => SetProperty(ref _maximumSourceUserTurns, value);
    }

    public bool SendOnlyNewMessages
    {
        get => _sendOnlyNewMessages;
        set => SetProperty(ref _sendOnlyNewMessages, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string CheckpointText
    {
        get => _checkpointText;
        private set => SetProperty(ref _checkpointText, value);
    }

    public string RequestPreview
    {
        get => _requestPreview;
        private set => SetProperty(ref _requestPreview, value);
    }

    public string PendingBody
    {
        get => _pendingBody;
        set => SetProperty(ref _pendingBody, value);
    }

    public string PendingTargetText
    {
        get => _pendingTargetText;
        private set => SetProperty(ref _pendingTargetText, value);
    }

    public bool IsGenerating
    {
        get => _isGenerating;
        private set
        {
            if (!SetProperty(ref _isGenerating, value))
            {
                return;
            }

            GenerateUpdateCommand.RaiseCanExecuteChanged();
            GenerateCompressionCommand.RaiseCanExecuteChanged();
            StopGenerationCommand.RaiseCanExecuteChanged();
        }
    }

    public async Task LoadAsync(
        string ownerId,
        string conversationId,
        string ownerLabel,
        CancellationToken cancellationToken = default,
        string? userIdentity = null)
    {
        var version = Interlocked.Increment(ref _loadVersion);
        var bankTask = _memoryBanks.GetAsync(ownerId, cancellationToken);
        var settingsTask = _workflow.GetSettingsAsync(ownerId, cancellationToken);
        var checkpointTask = _workflow.GetCheckpointAsync(
            ownerId,
            conversationId,
            cancellationToken);
        var draftsTask = _workflow.ListDraftsAsync(conversationId, cancellationToken);
        await Task.WhenAll(bankTask, settingsTask, checkpointTask, draftsTask);
        if (version != _loadVersion || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var previousOwnerId = _ownerId;
        var previousConversationId = _conversationId;
        var preserveUnsavedBody = IsBodyDirty
                                  && string.Equals(previousOwnerId, ownerId, StringComparison.Ordinal)
                                  && string.Equals(previousConversationId, conversationId, StringComparison.Ordinal);
        _ownerId = ownerId;
        _conversationId = conversationId;
        OwnerLabel = ownerLabel;
        _userIdentity = NormalizeUserIdentity(userIdentity);
        var bank = bankTask.Result;
        if (!preserveUnsavedBody)
        {
            ApplyLoadedBank(bank);
        }

        TargetTokens = (bank?.TargetTokens ?? 5000).ToString();
        ApplySettings(settingsTask.Result);
        ApplyCheckpoint(checkpointTask.Result);
        ApplyDraft(draftsTask.Result.FirstOrDefault());
        Status = preserveUnsavedBody
            ? LanguageRuntime.GetString("Memory.ReloadPreservedUnsaved")
            : bank is null
                ? LanguageRuntime.Format("Memory.NotSavedFormat", ownerLabel)
                : LanguageRuntime.Format("Memory.LoadedFormat", ownerLabel, bank.UpdatedAt);
        RaiseCommandStates();
    }

    public void Clear()
    {
        Interlocked.Increment(ref _loadVersion);
        _ownerId = null;
        _conversationId = null;
        _loadedBankRevision = 0;
        _userIdentity = "用户";
        OwnerLabel = LanguageRuntime.GetString("Memory.OwnerNone");
        ApplyLoadedBody(string.Empty);
        TargetTokens = "5000";
        ApplySettings(new MemoryWorkflowSettings { OwnerId = "__none__" });
        ApplyCheckpoint(null);
        ApplyDraft(null);
        Status = LanguageRuntime.GetString("Memory.SelectConversation");
        RaiseCommandStates();
    }

    public async Task TryAutoGenerateAsync(
        string ownerId,
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (IsCurrentOwner(ownerId, conversationId) && IsBodyDirty)
            {
                Status = LanguageRuntime.GetString("Memory.AutoSavePausedUnsaved");
                return;
            }

            var settings = ResolveGlobalDefaults(
                await _workflow.GetSettingsAsync(ownerId, cancellationToken));
            if (!settings.AutoGenerateEnabled)
            {
                return;
            }

            var existing = await _workflow.GetDraftAsync(
                ownerId,
                conversationId,
                MemoryDraftKind.Update,
                cancellationToken);
            if (existing is not null)
            {
                return;
            }

            var checkpoint = await _workflow.GetCheckpointAsync(
                ownerId,
                conversationId,
                cancellationToken);
            var messages = await _conversations.ListMessagesAsync(
                conversationId,
                cancellationToken);
            var unprocessedTurns = messages.Count(message =>
                message.SenderKind == MessageSenderKind.User
                && message.SequenceNo > (checkpoint?.LastSequenceNo ?? 0));
            if (unprocessedTurns < settings.UpdateIntervalTurns)
            {
                return;
            }

            var bank = await _memoryBanks.GetAsync(ownerId, cancellationToken);
            var plan = _prompts.BuildUpdate(
                ownerId,
                conversationId,
                bank?.Body ?? string.Empty,
                bank?.TargetTokens ?? 5000,
                settings,
                checkpoint,
                messages,
                await BuildSenderNamesAsync(cancellationToken),
                PromptMemorySubject(ownerId),
                _userIdentity) with
            {
                TargetBankRevision = bank?.Revision ?? 0
            };
            await GeneratePlanAsync(
                plan,
                ModelFunctionKind.MemoryUpdate,
                LanguageRuntime.GetString("Memory.AutoSaved"),
                cancellationToken,
                autoCommit: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (_ownerId == ownerId && _conversationId == conversationId)
            {
                Status = LanguageRuntime.Format("Memory.AutoSaveFailedFormat", LanguageRuntime.ErrorMessage(exception));
            }
        }
    }

    public async Task GenerateGroupMergeAsync(
        Character character,
        GroupChatSettings groupSettings,
        CancellationToken cancellationToken = default)
    {
        if (_ownerId is null || _conversationId is null)
        {
            return;
        }

        if (!MemoryOwnerIds.TryParseGroup(
                _ownerId,
                out var ownerConversationId,
                out var ownerCharacterId)
            || ownerCharacterId is not null
            || !string.Equals(
                ownerConversationId,
                _conversationId,
                StringComparison.Ordinal)
            || !string.Equals(
                groupSettings.ConversationId,
                _conversationId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                LanguageRuntime.GetString("Memory.GroupMergeConversationMismatch"));
        }

        var characterBankTask = _memoryBanks.GetAsync(character.Id, cancellationToken);
        var groupBankTask = _memoryBanks.GetAsync(_ownerId, cancellationToken);
        await Task.WhenAll(characterBankTask, groupBankTask);
        var characterBank = characterBankTask.Result;
        var groupBank = groupBankTask.Result;
        var groupRevision = EnsureDisplayedBankRevision(
            _ownerId,
            _conversationId,
            groupBank);
        var effectiveGroupSettings = ResolveGlobalDefaults(groupSettings);
        var plan = _prompts.BuildGroupMerge(
            character.Id,
            character.Name,
            _conversationId,
            characterBank?.Body ?? string.Empty,
            Body,
            characterBank?.TargetTokens ?? 5000,
            effectiveGroupSettings,
            _userIdentity) with
        {
            TargetBankRevision = characterBank?.Revision ?? 0,
            SourceBankRevision = groupRevision
        };
        await GeneratePlanAsync(
            plan,
            ModelFunctionKind.GroupMemoryMerge,
            LanguageRuntime.Format("Memory.MergeDraftGeneratedFormat", character.Name),
            cancellationToken);
    }

    private async Task SaveBodyAsync()
    {
        if (_ownerId is null
            || _conversationId is null
            || !TryReadTargetTokens(out var targetTokens))
        {
            return;
        }

        var ownerId = _ownerId;
        var conversationId = _conversationId;
        var bodySnapshot = Body;
        var revisionSnapshot = _loadedBankRevision;
        if (!await _memoryBanks.TrySaveBodyAsync(
                ownerId,
                bodySnapshot,
                targetTokens,
                revisionSnapshot))
        {
            if (IsCurrentOwner(ownerId, conversationId))
            {
                Status = LanguageRuntime.GetString("Memory.SaveConflict");
            }
            return;
        }

        var savedBank = await _memoryBanks.GetAsync(ownerId);
        if (savedBank is null)
        {
            if (IsCurrentOwner(ownerId, conversationId))
            {
                Status = LanguageRuntime.GetString("Memory.SaveConflict");
            }
            return;
        }

        var bodyUnchanged = string.Equals(
            Body,
            bodySnapshot,
            StringComparison.Ordinal);
        if (IsCurrentOwner(ownerId, conversationId))
        {
            if (bodyUnchanged)
            {
                // The saved database row is the new editor snapshot. This
                // updates body, baseline, revision and dirty state together.
                ApplyLoadedBank(savedBank);
            }
            else
            {
                // The database contains bodySnapshot, while Body contains a
                // later edit made during the await. Keep that later edit and
                // advance only the saved baseline/revision.
                _loadedBankRevision = savedBank.Revision;
                _bodyBaseline = savedBank.Body;
                IsBodyDirty = true;
            }
        }

        var stillCurrent = IsCurrentOwner(ownerId, conversationId);
        if (bodyUnchanged && stillCurrent)
        {
            BodySaved?.Invoke(
                this,
                new MemoryBodySavedEventArgs(ownerId, conversationId));
        }
        if (stillCurrent)
        {
            Status = bodyUnchanged
                ? LanguageRuntime.Format("Memory.DirectSavedFormat", OwnerLabel)
                : LanguageRuntime.GetString("Memory.DirectSavedWhileEditing");
        }
    }

    private async Task SaveSettingsAsync()
    {
        if (_ownerId is null
            || !int.TryParse(UpdateIntervalTurns, out var interval)
            || interval is < 1 or > 10000)
        {
            Status = LanguageRuntime.GetString("Memory.AutoIntervalRange");
            return;
        }

        if (!int.TryParse(MaximumSourceUserTurns, out var maximumSourceUserTurns)
            || maximumSourceUserTurns is < 1 or > 10000)
        {
            Status = LanguageRuntime.GetString("Memory.SourceLimitRange");
            return;
        }

        var settings = new MemoryWorkflowSettings
        {
            OwnerId = _ownerId,
            AutoGenerateEnabled = AutoGenerateEnabled,
            UpdateIntervalTurns = interval,
            MaximumSourceUserTurns = maximumSourceUserTurns,
            SendOnlyNewMessages = SendOnlyNewMessages,
            UpdateSystemPrompt =
                _globalPrompts.Get(GlobalPromptKey.MemoryUpdateSystem),
            UpdateUserTemplate =
                MemoryPromptDefaults.UpdateInput,
            CompressionSystemPrompt =
                _globalPrompts.Get(GlobalPromptKey.MemoryCompressionSystem),
            CompressionUserTemplate =
                MemoryPromptDefaults.CompressionInput
        };
        await _workflow.SaveSettingsAsync(settings);
        var sourceMode = SendOnlyNewMessages
            ? LanguageRuntime.GetString("Memory.SourceNewOnly")
            : LanguageRuntime.GetString("Memory.SourceAllowExisting");
        Status = AutoGenerateEnabled
            ? LanguageRuntime.Format(
                "Memory.WorkflowSavedFormat",
                interval,
                maximumSourceUserTurns,
                sourceMode)
            : LanguageRuntime.GetString("Memory.WorkflowDisabled");
    }

    private async Task PreviewUpdateAsync()
    {
        try
        {
            var plan = await BuildUpdatePlanAsync();
            RequestPreview = RenderPreview(plan, assignment: null);
            Status = LanguageRuntime.Format(
                "Memory.PreviewedFormat",
                plan.SourceUserTurns,
                plan.SourceThroughSequenceNo);
        }
        catch (Exception exception)
        {
            Status = LanguageRuntime.Format("Memory.PreviewFailedFormat", LanguageRuntime.ErrorMessage(exception));
        }
    }

    private async Task GenerateUpdateAsync()
    {
        try
        {
            var plan = await BuildUpdatePlanAsync();
            await GeneratePlanAsync(
                plan,
                ModelFunctionKind.MemoryUpdate,
                LanguageRuntime.GetString("Memory.UpdateDraftGenerated"));
        }
        catch (Exception exception)
        {
            Status = LanguageRuntime.Format("Memory.UpdateFailedFormat", LanguageRuntime.ErrorMessage(exception));
        }
    }

    private async Task GenerateCompressionAsync()
    {
        if (_ownerId is null || _conversationId is null)
        {
            return;
        }

        try
        {
            if (!TryReadTargetTokens(out var targetTokens))
            {
                return;
            }

            var settings = CreateSettingsSnapshot();
            var checkpoint = await _workflow.GetCheckpointAsync(
                _ownerId,
                _conversationId);
            var bank = await _memoryBanks.GetAsync(_ownerId);
            var bankRevision = EnsureDisplayedBankRevision(
                _ownerId,
                _conversationId,
                bank);
            var plan = _prompts.BuildCompression(
                _ownerId,
                _conversationId,
                Body,
                targetTokens,
                settings,
                checkpoint,
                PromptMemorySubject(_ownerId),
                _userIdentity) with
            {
                TargetBankRevision = bankRevision
            };
            await GeneratePlanAsync(
                plan,
                ModelFunctionKind.MemoryCompression,
                LanguageRuntime.GetString("Memory.CompressionDraftGenerated"));
        }
        catch (Exception exception)
        {
            Status = LanguageRuntime.Format("Memory.CompressionFailedFormat", LanguageRuntime.ErrorMessage(exception));
        }
    }

    private async Task<MemoryPromptPlan> BuildUpdatePlanAsync()
    {
        if (_ownerId is null || _conversationId is null)
        {
            throw new InvalidOperationException(
                LanguageRuntime.GetString("Memory.OwnerAndConversationRequired"));
        }

        if (!TryReadTargetTokens(out var targetTokens))
        {
            throw new InvalidOperationException(Status);
        }

        var checkpointTask = _workflow.GetCheckpointAsync(_ownerId, _conversationId);
        var messagesTask = _conversations.ListMessagesAsync(_conversationId);
        var namesTask = BuildSenderNamesAsync();
        var bankTask = _memoryBanks.GetAsync(_ownerId);
        await Task.WhenAll(checkpointTask, messagesTask, namesTask, bankTask);
        var bank = bankTask.Result;
        var bankRevision = EnsureDisplayedBankRevision(
            _ownerId,
            _conversationId,
            bank);
        return _prompts.BuildUpdate(
            _ownerId,
            _conversationId,
            Body,
            targetTokens,
            CreateSettingsSnapshot(),
            checkpointTask.Result,
            messagesTask.Result,
            namesTask.Result,
            PromptMemorySubject(_ownerId),
            _userIdentity) with
        {
            TargetBankRevision = bankRevision
        };
    }

    private async Task GeneratePlanAsync(
        MemoryPromptPlan plan,
        ModelFunctionKind functionKind,
        string successStatus,
        CancellationToken cancellationToken = default,
        bool autoCommit = false)
    {
        if (!await _generationGate.WaitAsync(0, cancellationToken))
        {
            if (IsCurrent(plan))
            {
                Status = LanguageRuntime.GetString("Memory.GenerationAlreadyRunning");
            }

            return;
        }

        _generationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        IsGenerating = true;
        try
        {
            var assignment = await _assignments.GetAsync(
                functionKind,
                _generationCancellation.Token)
                ?? throw new InvalidOperationException(
                    LanguageRuntime.Format(
                        "Memory.ModelUnassignedFormat",
                        FunctionLabel(functionKind)));
            var request = new ModelExecutionRequest(
                assignment.ProviderId,
                assignment.ModelId,
                [
                    new ProviderChatMessage("system", plan.SystemPrompt),
                    new ProviderChatMessage("user", plan.InputPayload)
                ],
                Math.Min(assignment.MaxOutputTokens, plan.TargetTokens + 1024),
                assignment.Temperature,
                assignment.TopP,
                assignment.ReasoningEnabled);
            var preview = RenderPreview(plan, assignment);
            if (IsCurrent(plan))
            {
                RequestPreview = preview;
                PendingBody = string.Empty;
                Status = LanguageRuntime.Format(
                    "Memory.GeneratingFormat",
                    assignment.ModelId,
                    FunctionLabel(functionKind));
            }

            var buffer = new StringBuilder();
            var sawReasoning = false;
            ProviderStreamEvent? completion = null;
            var generationOperationId =
                $"memory:{plan.TargetOwnerId}:{plan.Kind}";
            await _generationCoordinator.RunProviderAsync(
                generationOperationId,
                token => _gateway.StreamChatAsync(request, token),
                (streamEvent, _) =>
                {
                switch (streamEvent.Kind)
                {
                    case ProviderStreamEventKind.Reasoning:
                        sawReasoning = true;
                        if (IsCurrent(plan))
                        {
                            Status = LanguageRuntime.GetString("Memory.Thinking");
                        }

                        break;

                    case ProviderStreamEventKind.Content:
                        buffer.Append(streamEvent.Content);
                        if (IsCurrent(plan))
                        {
                            PendingBody = buffer.ToString();
                            Status = LanguageRuntime.Format("Memory.ReceivingFormat", FunctionLabel(functionKind));
                        }

                        break;

                    case ProviderStreamEventKind.Completed:
                        completion = streamEvent;
                        break;
                }

                    return ValueTask.CompletedTask;
                },
                _generationCancellation.Token);
            if (_generationCoordinator.GetState(generationOperationId).Status
                == ConversationGenerationStatus.Interrupted)
            {
                if (IsCurrent(plan))
                {
                    Status = LanguageRuntime.GetString("Memory.Stopped");
                }

                return;
            }

            if (buffer.Length == 0)
            {
                if (sawReasoning
                    && string.Equals(
                        completion?.FinishReason,
                        "length",
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        LanguageRuntime.GetString("Memory.NoBodyAfterThinking"));
                }

                throw new InvalidOperationException(
                    LanguageRuntime.GetString("Memory.NoBody"));
            }

            var draft = new MemoryUpdateDraft
            {
                TargetOwnerId = plan.TargetOwnerId,
                SourceConversationId = plan.SourceConversationId,
                Kind = plan.Kind,
                Body = buffer.ToString(),
                RequestPreview = preview,
                TargetTokens = plan.TargetTokens,
                SourceThroughSequenceNo = plan.SourceThroughSequenceNo,
                SourceUserTurns = plan.SourceUserTurns,
                SourceMessageCount = plan.SourceMessageCount,
                SourceDigest = plan.SourceDigest,
                TargetBankRevision = plan.TargetBankRevision,
                SourceBankRevision = plan.SourceBankRevision
            };
            await _workflow.SaveDraftAsync(draft, _generationCancellation.Token);
            if (autoCommit)
            {
                if (IsCurrent(plan) && IsBodyDirty)
                {
                    ApplyDraft(draft);
                    Status = LanguageRuntime.GetString("Memory.AutoSavePausedUnsaved");
                    return;
                }

                await _workflow.CommitDraftAsync(
                    draft.Id,
                    draft.Body,
                    draft.TargetTokens,
                    _generationCancellation.Token);
            }

            if (IsCurrent(plan))
            {
                if (autoCommit)
                {
                    ApplyDraft(null);
                    if (IsCurrentOwner(
                            draft.TargetOwnerId,
                            draft.SourceConversationId))
                    {
                        var committedBank = await _memoryBanks.GetAsync(
                            draft.TargetOwnerId,
                            _generationCancellation.Token);
                        if (!IsBodyDirty)
                        {
                            ApplyLoadedBank(committedBank);
                            ApplyCheckpoint(await _workflow.GetCheckpointAsync(
                                draft.TargetOwnerId,
                                draft.SourceConversationId,
                                _generationCancellation.Token));
                        }
                    }
                }
                else
                {
                    ApplyDraft(draft);
                }

                Status = successStatus;
            }
        }
        catch (OperationCanceledException)
            when (_generationCancellation.IsCancellationRequested)
        {
            if (IsCurrent(plan))
            {
                Status = LanguageRuntime.GetString("Memory.Stopped");
            }
        }
        finally
        {
            _generationCancellation.Dispose();
            _generationCancellation = null;
            IsGenerating = false;
            _generationGate.Release();
        }
    }

    private async Task SaveDraftAsync()
    {
        if (_pendingDraft is null)
        {
            return;
        }

        var draft = _pendingDraft;
        if (IsCurrentOwner(draft.TargetOwnerId, draft.SourceConversationId)
            && IsBodyDirty)
        {
            Status = LanguageRuntime.GetString("Memory.SaveConflict");
            return;
        }
        var targetTokens = draft.Kind == MemoryDraftKind.GroupMerge
            ? draft.TargetTokens
            : TryReadTargetTokens(out var editedTargetTokens)
                ? editedTargetTokens
                : 0;
        if (targetTokens == 0)
        {
            return;
        }

        var editedBody = PendingBody;
        await _workflow.CommitDraftAsync(draft.Id, editedBody, targetTokens);
        ApplyDraft(null);
        if (IsCurrentOwner(
                draft.TargetOwnerId,
                draft.SourceConversationId))
        {
            var committedBank = await _memoryBanks.GetAsync(draft.TargetOwnerId);
            if (!IsBodyDirty)
            {
                ApplyLoadedBank(committedBank);
                var checkpoint = await _workflow.GetCheckpointAsync(
                    draft.TargetOwnerId,
                    draft.SourceConversationId);
                ApplyCheckpoint(checkpoint);
            }
        }

        Status = draft.Kind switch
        {
            MemoryDraftKind.Update => LanguageRuntime.GetString("Memory.DraftSaved.Update"),
            MemoryDraftKind.Compression => LanguageRuntime.GetString("Memory.DraftSaved.Compression"),
            _ => LanguageRuntime.GetString("Memory.DraftSaved.Merge")
        };
    }

    private async Task DiscardDraftAsync()
    {
        if (_pendingDraft is null)
        {
            return;
        }

        await _workflow.DeleteDraftAsync(_pendingDraft.Id);
        ApplyDraft(null);
        Status = LanguageRuntime.GetString("Memory.DraftDiscarded");
    }

    private void StopGeneration() => _generationCancellation?.Cancel();

    private MemoryWorkflowSettings CreateSettingsSnapshot() =>
        ResolveGlobalDefaults(new MemoryWorkflowSettings
        {
            OwnerId = _ownerId ?? "__none__",
            AutoGenerateEnabled = AutoGenerateEnabled,
            UpdateIntervalTurns = int.TryParse(UpdateIntervalTurns, out var interval)
                ? interval
                : 20,
            MaximumSourceUserTurns =
                int.TryParse(MaximumSourceUserTurns, out var maximumSourceUserTurns)
                    ? maximumSourceUserTurns
                    : 20,
            SendOnlyNewMessages = SendOnlyNewMessages
        });

    private MemoryWorkflowSettings ResolveGlobalDefaults(
        MemoryWorkflowSettings settings)
    {
        settings.UpdateSystemPrompt =
            _globalPrompts.Get(GlobalPromptKey.MemoryUpdateSystem);
        settings.UpdateUserTemplate =
            MemoryPromptDefaults.UpdateInput;
        settings.CompressionSystemPrompt =
            _globalPrompts.Get(GlobalPromptKey.MemoryCompressionSystem);
        settings.CompressionUserTemplate =
            MemoryPromptDefaults.CompressionInput;

        return settings;
    }

    private GroupChatSettings ResolveGlobalDefaults(GroupChatSettings settings)
    {
        settings.MergeSystemPrompt =
            _globalPrompts.Get(GlobalPromptKey.GroupMemoryMergeSystem);
        settings.MergeUserTemplate =
            MemoryPromptDefaults.GroupMergeInput;

        return settings;
    }

    private async Task<IReadOnlyDictionary<string, string>> BuildSenderNamesAsync(
        CancellationToken cancellationToken = default) =>
        (await _characters.ListAsync(cancellationToken))
        .ToDictionary(character => character.Id, character => character.Name, StringComparer.Ordinal);

    private string PromptMemorySubject(string? ownerId) =>
        _ownerId == ownerId && !string.IsNullOrWhiteSpace(OwnerLabel)
            ? OwnerLabel
            : string.IsNullOrWhiteSpace(ownerId)
                ? LanguageRuntime.GetString("Memory.OwnerCurrent")
                : ownerId.Trim();

    private static string NormalizeUserIdentity(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "用户" : value.Trim();

    private bool TryReadTargetTokens(out int targetTokens)
    {
        if (!int.TryParse(TargetTokens, out targetTokens)
            || targetTokens is < 1000 or > 20000)
        {
            Status = LanguageRuntime.GetString("Memory.TargetRange");
            return false;
        }

        return true;
    }

    private bool IsCurrent(MemoryPromptPlan plan) =>
        IsCurrentOwner(plan.TargetOwnerId, plan.SourceConversationId);

    private bool IsCurrentOwner(string ownerId, string conversationId) =>
        string.Equals(_ownerId, ownerId, StringComparison.Ordinal)
        && string.Equals(_conversationId, conversationId, StringComparison.Ordinal);

    private void ApplySettings(MemoryWorkflowSettings settings)
    {
        AutoGenerateEnabled = settings.AutoGenerateEnabled;
        UpdateIntervalTurns = settings.UpdateIntervalTurns.ToString();
        MaximumSourceUserTurns = settings.MaximumSourceUserTurns.ToString();
        SendOnlyNewMessages = settings.SendOnlyNewMessages;
    }

    private void ApplyCheckpoint(MemoryCheckpoint? checkpoint)
    {
        CheckpointText = checkpoint is null
            ? LanguageRuntime.GetString("Memory.CheckpointAllNext")
            : LanguageRuntime.Format(
                "Memory.CheckpointFormat",
                checkpoint.LastSequenceNo,
                checkpoint.ProcessedUserTurns);
    }

    private void ApplyDraft(MemoryUpdateDraft? draft)
    {
        _pendingDraft = draft;
        PendingBody = draft?.Body ?? string.Empty;
        RequestPreview = draft?.RequestPreview
                         ?? LanguageRuntime.GetString("Memory.RequestPreviewHint");
        PendingTargetText = draft is null
            ? LanguageRuntime.GetString("Memory.NoPendingDraft")
            : LanguageRuntime.Format(
                "Memory.PendingTargetFormat",
                DraftLabel(draft.Kind),
                draft.TargetOwnerId,
                draft.TargetTokens,
                draft.SourceThroughSequenceNo);
        SaveDraftCommand.RaiseCanExecuteChanged();
        DiscardDraftCommand.RaiseCanExecuteChanged();
    }

    private void RaiseCommandStates()
    {
        OnPropertyChanged(nameof(IsLoaded));
        SaveBodyCommand.RaiseCanExecuteChanged();
        SaveSettingsCommand.RaiseCanExecuteChanged();
        PreviewUpdateCommand.RaiseCanExecuteChanged();
        GenerateUpdateCommand.RaiseCanExecuteChanged();
        GenerateCompressionCommand.RaiseCanExecuteChanged();
    }

    private void ApplyLoadedBody(string value)
    {
        _suppressBodyDirty = true;
        try
        {
            IsBodyDirty = false;
            Body = value;
            _bodyBaseline = value;
        }
        finally
        {
            _suppressBodyDirty = false;
        }
    }

    private void ApplyLoadedBank(MemoryBank? bank)
    {
        _loadedBankRevision = bank?.Revision ?? 0;
        ApplyLoadedBody(bank?.Body ?? string.Empty);
    }

    private long EnsureDisplayedBankRevision(
        string ownerId,
        string conversationId,
        MemoryBank? currentBank)
    {
        var currentRevision = currentBank?.Revision ?? 0;
        if (IsCurrentOwner(ownerId, conversationId)
            && currentRevision != _loadedBankRevision)
        {
            throw new InvalidOperationException(
                LanguageRuntime.GetString("Memory.SaveConflict"));
        }

        return IsCurrentOwner(ownerId, conversationId)
            ? _loadedBankRevision
            : currentRevision;
    }

    private static string RenderPreview(
        MemoryPromptPlan plan,
        ModelFunctionAssignment? assignment) =>
        JsonSerializer.Serialize(
            new
            {
                function = plan.Kind.ToString(),
                provider = assignment?.ProviderId ?? LanguageRuntime.GetString("Memory.NotCalled"),
                model = assignment?.ModelId ?? LanguageRuntime.GetString("Memory.NotCalled"),
                target_tokens = plan.TargetTokens,
                source_through_sequence = plan.SourceThroughSequenceNo,
                source_user_turns = plan.SourceUserTurns,
                messages = new[]
                {
                    new { role = "system", content = plan.SystemPrompt },
                    new { role = "user", content = plan.InputPayload }
                }
            },
            new JsonSerializerOptions { WriteIndented = true });

    private static string FunctionLabel(ModelFunctionKind kind) =>
        kind switch
        {
            ModelFunctionKind.MemoryUpdate => LanguageRuntime.GetString("Memory.Function.Update"),
            ModelFunctionKind.MemoryCompression => LanguageRuntime.GetString("Memory.Function.Compression"),
            ModelFunctionKind.GroupMemoryMerge => LanguageRuntime.GetString("Memory.Function.GroupMerge"),
            _ => kind.ToString()
        };

    private static string DraftLabel(MemoryDraftKind kind) =>
        kind switch
        {
            MemoryDraftKind.Update => LanguageRuntime.GetString("Memory.Draft.Update"),
            MemoryDraftKind.Compression => LanguageRuntime.GetString("Memory.Draft.Compression"),
            _ => LanguageRuntime.GetString("Memory.Draft.GroupMerge")
        };
}

public sealed class MemoryBodySavedEventArgs(
    string ownerId,
    string conversationId) : EventArgs
{
    public string OwnerId { get; } = ownerId;
    public string ConversationId { get; } = conversationId;
}
