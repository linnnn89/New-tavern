using System.Text;
using System.Text.Json;
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
    private string _ownerLabel = "未选择记忆";
    private string _body = string.Empty;
    private string _targetTokens = "5000";
    private bool _autoGenerateEnabled;
    private string _updateIntervalTurns = "20";
    private string _status = "选择会话后载入角色或群聊的独立记忆银行。";
    private string _checkpointText = "尚无处理检查点。";
    private string _requestPreview = "生成或预览后显示记忆 API 发送结构；不会包含 API Key。";
    private string _pendingBody = string.Empty;
    private string _pendingTargetText = "没有待保存草稿。";
    private MemoryUpdateDraft? _pendingDraft;
    private long _loadVersion;
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
                BodyChanged?.Invoke(this, EventArgs.Empty);
            }
        }
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
        CancellationToken cancellationToken = default)
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

        _ownerId = ownerId;
        _conversationId = conversationId;
        OwnerLabel = ownerLabel;
        var bank = bankTask.Result;
        Body = bank?.Body ?? string.Empty;
        TargetTokens = (bank?.TargetTokens ?? 5000).ToString();
        ApplySettings(settingsTask.Result);
        ApplyCheckpoint(checkpointTask.Result);
        ApplyDraft(draftsTask.Result.FirstOrDefault());
        Status = bank is null
            ? $"{ownerLabel}尚未保存记忆；默认目标 5000 tokens。"
            : $"已载入{ownerLabel}；最后保存 {bank.UpdatedAt:yyyy-MM-dd HH:mm}。";
        RaiseCommandStates();
    }

    public void Clear()
    {
        Interlocked.Increment(ref _loadVersion);
        _ownerId = null;
        _conversationId = null;
        OwnerLabel = "未选择记忆";
        Body = string.Empty;
        TargetTokens = "5000";
        ApplySettings(new MemoryWorkflowSettings { OwnerId = "__none__" });
        ApplyCheckpoint(null);
        ApplyDraft(null);
        Status = "选择会话后载入角色或群聊的独立记忆银行。";
        RaiseCommandStates();
    }

    public async Task TryAutoGenerateAsync(
        string ownerId,
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        try
        {
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
                await BuildSenderNamesAsync(cancellationToken));
            await GeneratePlanAsync(
                plan,
                ModelFunctionKind.MemoryUpdate,
                "已按阈值自动生成待保存记忆草稿；正文尚未覆盖。",
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (_ownerId == ownerId && _conversationId == conversationId)
            {
                Status = $"自动记忆草稿未生成：{exception.Message}";
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

        var characterBank = await _memoryBanks.GetAsync(character.Id, cancellationToken);
        var effectiveGroupSettings = ResolveGlobalDefaults(groupSettings);
        var plan = _prompts.BuildGroupMerge(
            character.Id,
            character.Name,
            _conversationId,
            characterBank?.Body ?? string.Empty,
            Body,
            characterBank?.TargetTokens ?? 5000,
            effectiveGroupSettings);
        await GeneratePlanAsync(
            plan,
            ModelFunctionKind.GroupMemoryMerge,
            $"已生成“{character.Name}”的合并草稿；角色记忆尚未覆盖。",
            cancellationToken);
    }

    private async Task SaveBodyAsync()
    {
        if (_ownerId is null || !TryReadTargetTokens(out var targetTokens))
        {
            return;
        }

        await _memoryBanks.SaveBodyAsync(_ownerId, Body, targetTokens);
        Status = $"已直接保存{OwnerLabel}；本操作不会推进聊天处理检查点。";
    }

    private async Task SaveSettingsAsync()
    {
        if (_ownerId is null
            || !int.TryParse(UpdateIntervalTurns, out var interval)
            || interval is < 1 or > 10000)
        {
            Status = "自动生成阈值必须是 1–10000 之间的用户轮次数。";
            return;
        }

        var settings = new MemoryWorkflowSettings
        {
            OwnerId = _ownerId,
            AutoGenerateEnabled = AutoGenerateEnabled,
            UpdateIntervalTurns = interval,
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
        Status = AutoGenerateEnabled
            ? $"已保存记忆工作流；每新增 {interval} 个用户轮次自动生成待保存草稿。"
            : "已保存记忆工作流；自动生成保持关闭。";
    }

    private async Task PreviewUpdateAsync()
    {
        try
        {
            var plan = await BuildUpdatePlanAsync();
            RequestPreview = RenderPreview(plan, assignment: null);
            Status =
                $"已预览增量更新：{plan.SourceUserTurns} 个用户轮次，处理到消息 #{plan.SourceThroughSequenceNo}；未调用模型。";
        }
        catch (Exception exception)
        {
            Status = $"无法生成更新预览：{exception.Message}";
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
                "已生成记忆更新草稿；编辑确认后再保存并推进检查点。");
        }
        catch (Exception exception)
        {
            Status = $"生成记忆更新失败：{exception.Message}";
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
            var plan = _prompts.BuildCompression(
                _ownerId,
                _conversationId,
                Body,
                targetTokens,
                settings,
                checkpoint);
            await GeneratePlanAsync(
                plan,
                ModelFunctionKind.MemoryCompression,
                "已生成压缩草稿；聊天处理检查点不会改变。");
        }
        catch (Exception exception)
        {
            Status = $"生成记忆压缩失败：{exception.Message}";
        }
    }

    private async Task<MemoryPromptPlan> BuildUpdatePlanAsync()
    {
        if (_ownerId is null || _conversationId is null)
        {
            throw new InvalidOperationException("尚未选择记忆所有者和来源会话。");
        }

        if (!TryReadTargetTokens(out var targetTokens))
        {
            throw new InvalidOperationException(Status);
        }

        var checkpointTask = _workflow.GetCheckpointAsync(_ownerId, _conversationId);
        var messagesTask = _conversations.ListMessagesAsync(_conversationId);
        var namesTask = BuildSenderNamesAsync();
        await Task.WhenAll(checkpointTask, messagesTask, namesTask);
        return _prompts.BuildUpdate(
            _ownerId,
            _conversationId,
            Body,
            targetTokens,
            CreateSettingsSnapshot(),
            checkpointTask.Result,
            messagesTask.Result,
            namesTask.Result);
    }

    private async Task GeneratePlanAsync(
        MemoryPromptPlan plan,
        ModelFunctionKind functionKind,
        string successStatus,
        CancellationToken cancellationToken = default)
    {
        if (!await _generationGate.WaitAsync(0, cancellationToken))
        {
            if (IsCurrent(plan))
            {
                Status = "已有记忆生成任务正在运行。";
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
                    $"“{FunctionLabel(functionKind)}”尚未分配模型。");
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
                Status = $"正在由 {assignment.ModelId} 生成{FunctionLabel(functionKind)}…";
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
                            Status = "模型正在思考；思考过程不会写入记忆草稿。";
                        }

                        break;

                    case ProviderStreamEventKind.Content:
                        buffer.Append(streamEvent.Content);
                        if (IsCurrent(plan))
                        {
                            PendingBody = buffer.ToString();
                            Status = $"正在接收{FunctionLabel(functionKind)}正文…";
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
                    Status = "记忆生成已停止；未保存不完整草稿。";
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
                        "输出上限在思考阶段耗尽，未生成可保存的记忆正文。");
                }

                throw new InvalidOperationException("模型没有返回可保存的记忆正文。");
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
                SourceUserTurns = plan.SourceUserTurns
            };
            await _workflow.SaveDraftAsync(draft, _generationCancellation.Token);
            if (IsCurrent(plan))
            {
                ApplyDraft(draft);
                Status = successStatus;
            }
        }
        catch (OperationCanceledException)
            when (_generationCancellation.IsCancellationRequested)
        {
            if (IsCurrent(plan))
            {
                Status = "记忆生成已停止；未保存不完整草稿。";
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
        if (draft.TargetOwnerId == _ownerId)
        {
            Body = editedBody;
            var checkpoint = await _workflow.GetCheckpointAsync(
                draft.TargetOwnerId,
                draft.SourceConversationId);
            ApplyCheckpoint(checkpoint);
        }

        Status = draft.Kind switch
        {
            MemoryDraftKind.Update =>
                "已保存更新后的记忆正文，并原子推进本会话处理检查点。",
            MemoryDraftKind.Compression =>
                "已保存压缩后的记忆正文；聊天处理检查点保持不变。",
            _ => "已把合并草稿保存到目标角色记忆；群聊独立记忆保持不变。"
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
        Status = "已丢弃待保存草稿；现有记忆正文和检查点均未改变。";
    }

    private void StopGeneration() => _generationCancellation?.Cancel();

    private MemoryWorkflowSettings CreateSettingsSnapshot() =>
        ResolveGlobalDefaults(new MemoryWorkflowSettings
        {
            OwnerId = _ownerId ?? "__none__",
            AutoGenerateEnabled = AutoGenerateEnabled,
            UpdateIntervalTurns = int.TryParse(UpdateIntervalTurns, out var interval)
                ? interval
                : 20
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

    private bool TryReadTargetTokens(out int targetTokens)
    {
        if (!int.TryParse(TargetTokens, out targetTokens)
            || targetTokens is < 1000 or > 20000)
        {
            Status = "记忆目标必须是 1000–20000 之间的整数 tokens。";
            return false;
        }

        return true;
    }

    private bool IsCurrent(MemoryPromptPlan plan) =>
        _conversationId == plan.SourceConversationId;

    private void ApplySettings(MemoryWorkflowSettings settings)
    {
        AutoGenerateEnabled = settings.AutoGenerateEnabled;
        UpdateIntervalTurns = settings.UpdateIntervalTurns.ToString();
    }

    private void ApplyCheckpoint(MemoryCheckpoint? checkpoint)
    {
        CheckpointText = checkpoint is null
            ? "尚无处理检查点；下次更新会读取本会话全部用户/角色消息。"
            : $"已处理到消息 #{checkpoint.LastSequenceNo}；累计 {checkpoint.ProcessedUserTurns} 个用户轮次。";
    }

    private void ApplyDraft(MemoryUpdateDraft? draft)
    {
        _pendingDraft = draft;
        PendingBody = draft?.Body ?? string.Empty;
        RequestPreview = draft?.RequestPreview
                         ?? "生成或预览后显示记忆 API 发送结构；不会包含 API Key。";
        PendingTargetText = draft is null
            ? "没有待保存草稿。"
            : $"{DraftLabel(draft.Kind)} · 目标 {draft.TargetOwnerId} · 目标 {draft.TargetTokens} tokens · 来源处理到 #{draft.SourceThroughSequenceNo}";
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

    private static string RenderPreview(
        MemoryPromptPlan plan,
        ModelFunctionAssignment? assignment) =>
        JsonSerializer.Serialize(
            new
            {
                function = plan.Kind.ToString(),
                provider = assignment?.ProviderId ?? "尚未调用",
                model = assignment?.ModelId ?? "尚未调用",
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
            ModelFunctionKind.MemoryUpdate => "记忆更新",
            ModelFunctionKind.MemoryCompression => "记忆压缩",
            ModelFunctionKind.GroupMemoryMerge => "群聊记忆合并",
            _ => kind.ToString()
        };

    private static string DraftLabel(MemoryDraftKind kind) =>
        kind switch
        {
            MemoryDraftKind.Update => "增量更新草稿",
            MemoryDraftKind.Compression => "压缩草稿",
            _ => "群聊合并草稿"
        };
}
