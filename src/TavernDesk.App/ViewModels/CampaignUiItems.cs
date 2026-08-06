using TavernDesk.App.Presentation;
using TavernDesk.Core.Models;

namespace TavernDesk.App.ViewModels;

public sealed record CampaignModelOption(
    string ProviderId,
    string ProviderName,
    string ModelId,
    string ModelName,
    int ContextLimit,
    int MaxOutputTokens)
{
    public string DisplayLabel => $"{ProviderName} / {ModelName}";

    public CampaignModelRoute ToRoute(
        double temperature = 0.8,
        double topP = 1) =>
        new(
            ProviderId,
            ModelId,
            ContextLimit,
            MaxOutputTokens,
            temperature,
            topP);
}

public sealed record CampaignFlowChoice(
    CampaignFlowPreset Value,
    string Name,
    string Description);

public sealed record CampaignGmChoice(
    CampaignGmKind Value,
    string Name,
    string Description);

public sealed record CampaignUserParticipationChoice(
    bool UserAlsoPlayer,
    string Name,
    string Description);

public sealed record CampaignSummaryItemViewModel(CampaignSummary Summary)
{
    public string Id => Summary.Id;
    public string Title => Summary.Title;
    public int CurrentRound => Summary.CurrentRound;
    public int ParticipantCount => Summary.ParticipantCount;
    public DateTimeOffset UpdatedAt => Summary.UpdatedAt;
    public string StatusLabel => Summary.Status switch
    {
        CampaignStatus.Draft => "大厅草稿",
        CampaignStatus.Active => "进行中",
        CampaignStatus.Completed => "已完成",
        CampaignStatus.Archived => "已归档",
        _ => Summary.Status.ToString()
    };
}

public sealed class CampaignCharacterChoiceViewModel : ViewModelBase
{
    private bool _isSelected;
    private bool _includeMemory;
    private bool _includeOriginalWorldKnowledge;
    private bool _isSelectionEnabled = true;
    private CampaignModelOption? _selectedRoute;

    public CampaignCharacterChoiceViewModel(Character character)
    {
        Character = character;
    }

    public Character Character { get; }
    public string Name => Character.Name;
    public string Description => Character.Description;
    public string AvatarPath => Character.AvatarPath;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool IncludeMemory
    {
        get => _includeMemory;
        set => SetProperty(ref _includeMemory, value);
    }

    public bool IncludeOriginalWorldKnowledge
    {
        get => _includeOriginalWorldKnowledge;
        set => SetProperty(ref _includeOriginalWorldKnowledge, value);
    }

    public bool IsSelectionEnabled
    {
        get => _isSelectionEnabled;
        set => SetProperty(ref _isSelectionEnabled, value);
    }

    public CampaignModelOption? SelectedRoute
    {
        get => _selectedRoute;
        set => SetProperty(ref _selectedRoute, value);
    }
}

public sealed class CampaignSeatViewModel : ViewModelBase
{
    private CampaignModelOption? _selectedRoute;
    private string _roundStatus = "等待行动";
    private bool _showActionButton;
    private bool _canGenerateAction;
    private string _actionHelpText = string.Empty;

    public CampaignSeatViewModel(CampaignParticipant participant)
    {
        Participant = participant;
    }

    public CampaignParticipant Participant { get; }
    public string Id => Participant.Id;
    public string Name => Participant.DisplayName;
    public bool IsAi => Participant.Kind == CampaignParticipantKind.Ai;
    public string KindLabel =>
        Participant.Kind == CampaignParticipantKind.User ? "USER" : "AI";
    public string ActionButtonText => $"让 {Name} 行动";

    public CampaignModelOption? SelectedRoute
    {
        get => _selectedRoute;
        set => SetProperty(ref _selectedRoute, value);
    }

    public string RoundStatus
    {
        get => _roundStatus;
        set => SetProperty(ref _roundStatus, value);
    }

    public bool ShowActionButton
    {
        get => _showActionButton;
        set => SetProperty(ref _showActionButton, value);
    }

    public bool CanGenerateAction
    {
        get => _canGenerateAction;
        set => SetProperty(ref _canGenerateAction, value);
    }

    public string ActionHelpText
    {
        get => _actionHelpText;
        set => SetProperty(ref _actionHelpText, value);
    }
}

public sealed record CampaignContextSectionItemViewModel(
    string Title,
    string TokenText,
    string StateText);

public sealed record CampaignContextPreviewItemViewModel(
    string Title,
    string BudgetText,
    string StatusText,
    IReadOnlyList<CampaignContextSectionItemViewModel> Sections);

public sealed record CampaignEventItemViewModel(
    CampaignEvent Event,
    string ActorName,
    string KindLabel,
    string StatusLabel,
    string DisplayContent,
    bool CanRetry)
{
    public string RetryButtonText => $"重试 {ActorName} 的本回合行动";
    public string RetryHelpText =>
        $"重新调用 {ActorName} 当前使用的模型；失败记录会保留，新结果另存为一次重试。";
}

public sealed record CampaignSeatActionState(
    bool ShowButton,
    bool CanAct,
    string HelpText)
{
    public static CampaignSeatActionState Create(
        CampaignAggregate aggregate,
        CampaignParticipant participant)
    {
        if (participant.Kind != CampaignParticipantKind.Ai
            || !participant.IsEnabled)
        {
            return new CampaignSeatActionState(
                ShowButton: false,
                CanAct: false,
                "USER 席位不调用角色模型。");
        }

        if (aggregate.Campaign.FlowPreset
            == CampaignFlowPreset.BlindSubmission)
        {
            return new CampaignSeatActionState(
                ShowButton: false,
                CanAct: false,
                "秘密同投会从“当前步骤”一次并发生成全部 AI 行动，确保彼此不可见。");
        }

        if (aggregate.Campaign.Status != CampaignStatus.Active)
        {
            return new CampaignSeatActionState(
                ShowButton: true,
                CanAct: false,
                "这局跑团当前不是进行中状态。");
        }

        if (aggregate.Campaign.Phase != CampaignPhase.AwaitingActions)
        {
            return new CampaignSeatActionState(
                ShowButton: true,
                CanAct: false,
                "当前正在等待 GM 裁定，不能再增加玩家行动。");
        }

        var latest = LatestAction(aggregate, participant.Id);
        if (latest is not null)
        {
            var help = latest.GenerationStatus switch
            {
                CampaignGenerationStatus.Completed =>
                    $"{participant.DisplayName} 本回合已经完成行动。",
                CampaignGenerationStatus.Failed
                    or CampaignGenerationStatus.Interrupted =>
                    $"{participant.DisplayName} 的生成未完成；请在跑团记录中重试原行动。",
                _ => $"{participant.DisplayName} 的行动正在生成或等待处理。"
            };
            return new CampaignSeatActionState(
                ShowButton: true,
                CanAct: false,
                help);
        }

        if (aggregate.Campaign.FlowPreset
            == CampaignFlowPreset.StrictInitiative)
        {
            var enabled = aggregate.Participants
                .Where(item => item.IsEnabled)
                .OrderBy(item => item.SortIndex)
                .ToArray();
            var current =
                enabled[aggregate.Campaign.CurrentTurnIndex % enabled.Length];
            if (current.Id != participant.Id)
            {
                return new CampaignSeatActionState(
                    ShowButton: true,
                    CanAct: false,
                    $"严格先攻当前轮到 {current.DisplayName}，尚未轮到 {participant.DisplayName}。");
            }
        }

        return new CampaignSeatActionState(
            ShowButton: true,
            CanAct: true,
            $"调用该席位的模型，让 {participant.DisplayName} 根据当前可见记录提交本回合行动。");
    }

    private static CampaignEvent? LatestAction(
        CampaignAggregate aggregate,
        string participantId) =>
        aggregate.Events
            .Where(item =>
                item.RoundNo == aggregate.Campaign.CurrentRound
                && item.Kind == CampaignEventKind.PlayerIntent
                && item.ActorId == participantId)
            .OrderBy(item => item.SequenceNo)
            .LastOrDefault();
}

public sealed record CampaignGameUiState(
    bool HasUserSeat,
    bool HasPendingUserJoin,
    bool CanScheduleUserJoin,
    bool ShowUserActionSection,
    bool UserSeatCanAct,
    bool ShowBlindAiAction,
    bool CanGenerateBlindAiActions,
    bool ShowResolveSection,
    string CurrentStepTitle,
    string CurrentStepDescription,
    string CurrentStepProgressText,
    string GmModeText,
    string ParticipationModeText,
    string UserActionHelpText,
    string BlindAiActionHelpText,
    string ResolveHelpText)
{
    public static CampaignGameUiState Empty { get; } = new(
        false,
        false,
        false,
        false,
        false,
        false,
        false,
        false,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty);

    public static CampaignGameUiState Create(CampaignAggregate aggregate)
    {
        var campaign = aggregate.Campaign;
        var enabled = aggregate.Participants
            .Where(item => item.IsEnabled)
            .OrderBy(item => item.SortIndex)
            .ToArray();
        var userSeat = enabled.FirstOrDefault(item =>
            item.Kind == CampaignParticipantKind.User);
        var pendingUser = aggregate.Participants.FirstOrDefault(item =>
            item.Kind == CampaignParticipantKind.User && !item.IsEnabled);
        var latestActions = aggregate.Events
            .Where(item =>
                item.RoundNo == campaign.CurrentRound
                && item.Kind == CampaignEventKind.PlayerIntent)
            .GroupBy(item => item.ActorId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(item => item.SequenceNo).Last(),
                StringComparer.Ordinal);
        var completed = latestActions.Values.Count(item =>
            item.GenerationStatus == CampaignGenerationStatus.Completed
            && item.IsLocked);
        var failures = latestActions.Values.Count(item =>
            item.GenerationStatus is (
                CampaignGenerationStatus.Failed
                or CampaignGenerationStatus.Interrupted));
        var latestGmResolution = aggregate.Events
            .Where(item =>
                item.RoundNo == campaign.CurrentRound
                && item.Kind == CampaignEventKind.GmResolution)
            .OrderBy(item => item.SequenceNo)
            .LastOrDefault();
        var gmResolutionFailed =
            campaign.GmKind == CampaignGmKind.Ai
            && latestGmResolution?.GenerationStatus is (
                CampaignGenerationStatus.Failed
                or CampaignGenerationStatus.Interrupted);
        var userHasAction = userSeat is not null
                            && latestActions.ContainsKey(userSeat.Id);
        var current = campaign.FlowPreset
                      == CampaignFlowPreset.StrictInitiative
                      && enabled.Length > 0
            ? enabled[campaign.CurrentTurnIndex % enabled.Length]
            : null;
        var userSeatCanAct =
            campaign.Status == CampaignStatus.Active
            && campaign.Phase == CampaignPhase.AwaitingActions
            && userSeat is not null
            && !userHasAction
            && (current is null || current.Id == userSeat.Id);
        var showUserActionSection =
            campaign.Phase == CampaignPhase.AwaitingActions
            && userSeat is not null
            && !userHasAction
            && (current is null || current.Id == userSeat.Id);
        var unattemptedAiCount = enabled.Count(item =>
            item.Kind == CampaignParticipantKind.Ai
            && !latestActions.ContainsKey(item.Id));
        var showBlindAiAction =
            campaign.FlowPreset == CampaignFlowPreset.BlindSubmission
            && campaign.Phase == CampaignPhase.AwaitingActions
            && unattemptedAiCount > 0;
        var canGenerateBlindAiActions =
            campaign.Status == CampaignStatus.Active
            && showBlindAiAction
            && failures == 0;
        var showResolveSection =
            campaign.Status == CampaignStatus.Active
            && campaign.Phase == CampaignPhase.ReadyForResolution;
        var stepTitle = StepTitle(
            campaign,
            current,
            failures,
            gmResolutionFailed);
        var stepDescription = StepDescription(
            campaign,
            current,
            failures,
            gmResolutionFailed,
            userSeat is not null);
        var progress = gmResolutionFailed
            ? "上一次 AI GM 请求未完成；原失败记录已保留。"
            : campaign.Phase == CampaignPhase.ReadyForResolution
            ? $"已收齐 {enabled.Length} 个玩家席位的行动，可以交给 GM。"
            : $"{completed}/{enabled.Length} 个玩家席位已完成"
              + (failures > 0 ? $" · {failures} 个需要重试" : string.Empty);
        var userHelp = userSeatCanAct
            ? "提交你的本回合行动；系统会自动附加一枚 1d20，并按行动本身的可见性一起保存。"
            : UserActionUnavailableReason(
                campaign,
                current,
                userSeat,
                userHasAction);
        var blindHelp = canGenerateBlindAiActions
            ? $"并发调用 {unattemptedAiCount} 个 AI 席位；它们基于同一冻结记录且彼此看不到本轮行动。"
            : failures > 0
                ? "至少一个 AI 行动失败；必须先重试失败记录。"
                : "当前没有需要生成的秘密 AI 行动。";
        var resolveHelp = showResolveSection
            ? gmResolutionFailed
                ? "重新调用当前 GM 模型；也可先在下方切换模型。失败记录不会被删除。"
                : campaign.GmKind == CampaignGmKind.Ai
                ? "调用已选择的 GM 模型，结合每条行动末尾的自动 1d20 统一裁定；GM 不会替玩家决定下一步。"
                : "把你输入的内容作为本回合 GM 裁定；若未填写“下一轮评定参考”，系统会附加一段灵活的通用说明。"
            : "尚未收齐本阶段要求的全部玩家行动。";
        return new CampaignGameUiState(
            userSeat is not null,
            pendingUser is not null,
            campaign.Status == CampaignStatus.Active
            && userSeat is null
            && pendingUser is null,
            showUserActionSection,
            userSeatCanAct,
            showBlindAiAction,
            canGenerateBlindAiActions,
            showResolveSection,
            stepTitle,
            stepDescription,
            progress,
            campaign.GmKind == CampaignGmKind.Ai
                ? "AI GM 主持 · 裁定前由你确认"
                : "你担任 GM · 裁定由你填写",
            userSeat is not null
                ? "你正在作为 USER 玩家参与"
                : pendingUser is not null
                    ? "你将在下一回合加入"
                    : "你正在观看 AI 演出",
            userHelp,
            blindHelp,
            resolveHelp);
    }

    private static string StepTitle(
        Campaign campaign,
        CampaignParticipant? current,
        int failures,
        bool gmResolutionFailed)
    {
        if (gmResolutionFailed)
        {
            return "重试 AI GM 裁定";
        }

        if (failures > 0)
        {
            return "先处理生成失败";
        }

        return campaign.Phase switch
        {
            CampaignPhase.AwaitingActions
                when campaign.FlowPreset
                     == CampaignFlowPreset.BlindSubmission =>
                "收集秘密行动",
            CampaignPhase.AwaitingActions
                when current is not null =>
                $"轮到 {current.DisplayName} 行动",
            CampaignPhase.AwaitingActions => "收集本回合行动",
            CampaignPhase.ReadyForResolution
                when campaign.GmKind == CampaignGmKind.Ai =>
                "确认并生成 AI GM 裁定",
            CampaignPhase.ReadyForResolution => "填写本回合 GM 裁定",
            CampaignPhase.Paused => "跑团已暂停",
            CampaignPhase.Completed => "跑团已完成",
            _ => "正在更新跑团状态"
        };
    }

    private static string StepDescription(
        Campaign campaign,
        CampaignParticipant? current,
        int failures,
        bool gmResolutionFailed,
        bool hasUserSeat)
    {
        if (gmResolutionFailed)
        {
            return "上一次 AI GM 裁定未完成。可以直接重试，或先在下方切换 GM 模型；本回合不能跳过。";
        }

        if (failures > 0)
        {
            return "失败席位不会被自动跳过。必须先在跑团记录中重试，或先为该席位切换模型。";
        }

        return campaign.Phase switch
        {
            CampaignPhase.AwaitingActions
                when campaign.FlowPreset
                     == CampaignFlowPreset.BlindSubmission =>
                hasUserSeat
                    ? "你单独提交自己的行动；AI 玩家由下方按钮一次并发生成，彼此看不到本轮选择。"
                    : "使用下方按钮一次并发生成全部 AI 行动；它们基于同一冻结记录，彼此看不到本轮选择。",
            CampaignPhase.AwaitingActions
                when current is not null =>
                $"严格先攻只开放 {current.DisplayName} 的行动；该行动完成并经 GM 裁定后才轮到下一席。",
            CampaignPhase.AwaitingActions =>
                hasUserSeat
                    ? "提交你的行动，或在左侧选择任一尚未行动的 AI。后行动者能看到先前公开提议。"
                    : "在左侧选择任一尚未行动的 AI。后行动者能看到先前公开提议。",
            CampaignPhase.ReadyForResolution
                when campaign.GmKind == CampaignGmKind.Ai =>
                "所有玩家行动已经锁定。确认后才会调用 AI GM，不会自动产生请求。",
            CampaignPhase.ReadyForResolution =>
                "所有玩家行动已经锁定。填写裁定后进入下一行动阶段。",
            _ => "请等待当前状态完成，或重新载入本局查看最新进度。"
        };
    }

    private static string UserActionUnavailableReason(
        Campaign campaign,
        CampaignParticipant? current,
        CampaignParticipant? userSeat,
        bool userHasAction)
    {
        if (userSeat is null)
        {
            return "当前跑团没有启用 USER 玩家席位。";
        }

        if (userHasAction)
        {
            return "你本回合已经提交行动。";
        }

        if (campaign.Phase != CampaignPhase.AwaitingActions)
        {
            return "当前不是玩家行动阶段。";
        }

        if (current is not null && current.Id != userSeat.Id)
        {
            return $"严格先攻当前轮到 {current.DisplayName}。";
        }

        return "当前状态暂时不能提交 USER 行动。";
    }
}
