using TavernDesk.App.Localization;
using TavernDesk.App.Presentation;
using TavernDesk.Core.Flow;
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
        double topP = 1,
        int maximumOutputTokens = int.MaxValue) =>
        new(
            ProviderId,
            ModelId,
            ContextLimit,
            Math.Min(MaxOutputTokens, maximumOutputTokens),
            temperature,
            topP);
}

public sealed record CampaignFlowChoice(
    CampaignFlowPreset Value,
    string Name,
    string Description);

public sealed record CampaignNarrativePermissionChoice(
    CampaignNarrativePermission Value,
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
        CampaignStatus.Draft => LanguageRuntime.GetString("Campaign.Status.Draft"),
        CampaignStatus.Active => LanguageRuntime.GetString("Campaign.Status.Active"),
        CampaignStatus.Completed => LanguageRuntime.GetString("Campaign.Status.Completed"),
        CampaignStatus.Archived => LanguageRuntime.GetString("Campaign.Status.Archived"),
        _ => Summary.Status.ToString()
    };
}

public sealed class CampaignCharacterChoiceViewModel : ViewModelBase
{
    private bool _isSelected;
    private bool _includeMemory;
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
    private string _roundStatus = LanguageRuntime.GetString("Campaign.Round.Waiting");
    private bool _showActionButton;
    private bool _canGenerateAction;
    private bool _isRetryAction;
    private string? _retryEventId;
    private string _actionHelpText = string.Empty;
    private bool _isCurrentTurn;

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
    public string ActionButtonText => IsRetryAction
        ? LanguageRuntime.Format("Campaign.Action.RetryFormat", Name)
        : LanguageRuntime.Format("Campaign.Action.ActFormat", Name);

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

    public bool IsRetryAction
    {
        get => _isRetryAction;
        set
        {
            if (SetProperty(ref _isRetryAction, value))
            {
                OnPropertyChanged(nameof(ActionButtonText));
            }
        }
    }

    public string? RetryEventId
    {
        get => _retryEventId;
        set => SetProperty(ref _retryEventId, value);
    }

    public string ActionHelpText
    {
        get => _actionHelpText;
        set => SetProperty(ref _actionHelpText, value);
    }

    public bool IsCurrentTurn
    {
        get => _isCurrentTurn;
        set => SetProperty(ref _isCurrentTurn, value);
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
    bool CanRetry,
    IReadOnlyList<CampaignEvent>? Candidates = null,
    int ActiveCandidateIndex = 0)
{
    public string RetryButtonText => LanguageRuntime.Format(
        "Campaign.Action.RetryTurnFormat",
        ActorName);
    public string RetryHelpText =>
        LanguageRuntime.Format("Campaign.Action.RetryHelpFormat", ActorName);
    public bool HasCandidates => Candidates is { Count: > 1 };
    public string CandidateNavigationLabel => HasCandidates
        ? $"{ActiveCandidateIndex + 1}/{Candidates!.Count}"
        : string.Empty;
    public string CandidateHelpText => Event.GenerationStatus
        == CampaignGenerationStatus.Completed
        && Event.EndReason == CampaignEndReason.Normal
        ? LanguageRuntime.GetString("Campaign.Candidate.Valid")
        : LanguageRuntime.GetString("Campaign.Candidate.Invalid");
}

public sealed record CampaignSeatActionState(
    bool ShowButton,
    bool CanAct,
    string HelpText,
    bool IsRetry = false,
    string? RetryEventId = null)
{
    public static CampaignSeatActionState Create(
        CampaignAggregate aggregate,
        CampaignParticipant participant) =>
        Create(
            aggregate,
            participant,
            CampaignFlowEngineFactory.CreateDefault().Inspect(aggregate));

    public static CampaignSeatActionState Create(
        CampaignAggregate aggregate,
        CampaignParticipant participant,
        CampaignFlowSnapshot snapshot,
        CampaignActionPlan? participantPlan = null)
    {
        if (participant.Kind != CampaignParticipantKind.Ai
            || !participant.IsEnabled)
        {
            return new CampaignSeatActionState(
                ShowButton: false,
                CanAct: false,
                LanguageRuntime.GetString("Campaign.Action.UserNoModel"));
        }

        if (snapshot.ActionPlan.ExecutionMode
            == CampaignActionExecutionMode.Parallel)
        {
            return new CampaignSeatActionState(
                ShowButton: false,
                CanAct: false,
                LanguageRuntime.GetString("Campaign.Action.BlindBatchOnly"));
        }

        var latest = LatestAction(aggregate, participant.Id);
        if (latest is not null)
        {
            participantPlan ??= CampaignFlowEngineFactory.CreateDefault()
                .PlanAction(aggregate, participant.Id);
            var help = latest.GenerationStatus switch
            {
                CampaignGenerationStatus.Completed =>
                    LanguageRuntime.Format("Campaign.Action.CompletedFormat", participant.DisplayName),
                CampaignGenerationStatus.Failed
                    or CampaignGenerationStatus.Interrupted =>
                    LanguageRuntime.Format("Campaign.Action.IncompleteFormat", participant.DisplayName),
                _ => LanguageRuntime.Format("Campaign.Action.BusyFormat", participant.DisplayName)
            };
            var canRetry = latest.GenerationStatus is
                               CampaignGenerationStatus.Failed
                               or CampaignGenerationStatus.Interrupted
                           && participantPlan.BlockReason
                           == CampaignFlowBlockReason.FailedAttemptRequiresRetry
                           && participantPlan.AllowedParticipantIds.Contains(
                               participant.Id,
                               StringComparer.Ordinal);
            return new CampaignSeatActionState(
                ShowButton: true,
                CanAct: canRetry,
                help,
                IsRetry: canRetry,
                RetryEventId: canRetry ? latest.Id : null);
        }

        var canAct = snapshot.ActionPlan.CanSubmit
                     && snapshot.ActionPlan.AllowedParticipantIds.Contains(
                         participant.Id,
                         StringComparer.Ordinal);
        if (!canAct)
        {
            var currentName = aggregate.Participants.FirstOrDefault(item =>
                string.Equals(
                    item.Id,
                    snapshot.CurrentParticipantId,
                    StringComparison.Ordinal))?.DisplayName;
            return new CampaignSeatActionState(
                ShowButton: true,
                CanAct: false,
                snapshot.CurrentParticipantId is not null
                    ? LanguageRuntime.Format(
                        "Campaign.Action.NotTurnFormat",
                        currentName ?? snapshot.CurrentParticipantId,
                        participant.DisplayName)
                    : LanguageRuntime.Format(
                        "Campaign.Action.BlockedFormat",
                        snapshot.ActionPlan.BlockReason));
        }

        return new CampaignSeatActionState(
            ShowButton: true,
            CanAct: true,
            LanguageRuntime.Format("Campaign.Action.ModelHelpFormat", participant.DisplayName));
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
        => Create(
            aggregate,
            CampaignFlowEngineFactory.CreateDefault().Inspect(aggregate));

    public static CampaignGameUiState Create(
        CampaignAggregate aggregate,
        CampaignFlowSnapshot snapshot)
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
        var candidateIds = snapshot.ResolutionPlan.CandidateResolutionIds
            .ToHashSet(StringComparer.Ordinal);
        var gmCandidates = aggregate.Events
            .Where(item => candidateIds.Contains(item.Id))
            .ToArray();
        var gmCandidatePending =
            campaign.GmKind == CampaignGmKind.Ai
            && campaign.Phase == CampaignPhase.ReadyForResolution
            && snapshot.ResolutionPlan.CanCommit;
        var userHasAction = userSeat is not null
                            && latestActions.ContainsKey(userSeat.Id);
        var current = snapshot.CurrentParticipantId is null
            ? null
            : enabled.FirstOrDefault(item => item.Id == snapshot.CurrentParticipantId);
        var userSeatCanAct =
            campaign.Status == CampaignStatus.Active
            && userSeat is not null
            && snapshot.ActionPlan.CanSubmit
            && snapshot.ActionPlan.AllowedParticipantIds.Contains(userSeat.Id, StringComparer.Ordinal);
        var showUserActionSection =
            userSeat is not null && userSeatCanAct;
        var unattemptedAiCount = enabled.Count(item =>
            item.Kind == CampaignParticipantKind.Ai
            && !latestActions.ContainsKey(item.Id));
        var showBlindAiAction =
            snapshot.ActionPlan.ExecutionMode == CampaignActionExecutionMode.Parallel
            && campaign.Phase == CampaignPhase.AwaitingActions
            && unattemptedAiCount > 0;
        var canGenerateBlindAiActions =
            campaign.Status == CampaignStatus.Active
            && showBlindAiAction
            && failures == 0;
        var showResolveSection =
            campaign.Status == CampaignStatus.Active
            && snapshot.Stage is CampaignFlowStage.ReadyForResolution
                or CampaignFlowStage.RetryingResolution
                or CampaignFlowStage.SelectingResolutionCandidate;
        var stepTitle = StepTitle(
            campaign,
            snapshot.ActionPlan.ExecutionMode,
            current,
            failures,
            gmResolutionFailed,
            gmCandidatePending);
        var stepDescription = StepDescription(
            campaign,
            snapshot.ActionPlan.ExecutionMode,
            current,
            failures,
            gmResolutionFailed,
            gmCandidatePending,
            userSeat is not null);
        var progress = gmCandidatePending
            ? LanguageRuntime.Format("Campaign.Progress.GmCandidatesFormat", gmCandidates.Length)
            : gmResolutionFailed
            ? LanguageRuntime.GetString("Campaign.Progress.GmFailed")
            : campaign.Phase == CampaignPhase.ReadyForResolution
            ? LanguageRuntime.Format("Campaign.Progress.ReadyForGmFormat", enabled.Length)
            : LanguageRuntime.Format("Campaign.Progress.CompletedFormat", completed, enabled.Length)
              + (failures > 0
                  ? LanguageRuntime.Format("Campaign.Progress.RetryCountFormat", failures)
                  : string.Empty);
        var userHelp = userSeatCanAct
            ? LanguageRuntime.GetString("Campaign.UserAction.Help")
            : UserActionUnavailableReason(
                campaign,
                current,
                userSeat,
                userHasAction);
        var blindHelp = canGenerateBlindAiActions
            ? LanguageRuntime.Format("Campaign.Blind.GenerateHelpFormat", unattemptedAiCount)
            : failures > 0
                ? LanguageRuntime.GetString("Campaign.Blind.RetryFirst")
                : LanguageRuntime.GetString("Campaign.Blind.None");
        var resolveHelp = showResolveSection
            ? gmCandidatePending
                ? LanguageRuntime.GetString("Campaign.Resolve.SelectCandidate")
                : gmResolutionFailed
                ? LanguageRuntime.GetString("Campaign.Resolve.RetryHelp")
                : campaign.GmKind == CampaignGmKind.Ai
                ? LanguageRuntime.GetString("Campaign.Resolve.AiHelp")
                : LanguageRuntime.GetString("Campaign.Resolve.HumanHelp")
            : LanguageRuntime.GetString("Campaign.Resolve.Waiting");
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
                ? LanguageRuntime.GetString("Campaign.Gm.Ai")
                : LanguageRuntime.GetString("Campaign.Gm.Human"),
            userSeat is not null
                ? LanguageRuntime.GetString("Campaign.User.Participating")
                : pendingUser is not null
                    ? LanguageRuntime.GetString("Campaign.User.JoinNext")
                    : LanguageRuntime.GetString("Campaign.User.Watching"),
            userHelp,
            blindHelp,
            resolveHelp);
    }

    private static string StepTitle(
        Campaign campaign,
        CampaignActionExecutionMode executionMode,
        CampaignParticipant? current,
        int failures,
        bool gmResolutionFailed,
        bool gmCandidatePending)
    {
        if (gmCandidatePending)
        {
            return LanguageRuntime.GetString("Campaign.Step.SelectGmCandidate");
        }

        if (gmResolutionFailed)
        {
            return LanguageRuntime.GetString("Campaign.Step.RetryGm");
        }

        if (failures > 0)
        {
            return LanguageRuntime.GetString("Campaign.Step.HandleFailure");
        }

        return campaign.Phase switch
        {
            CampaignPhase.AwaitingActions
                when executionMode == CampaignActionExecutionMode.Parallel =>
                LanguageRuntime.GetString("Campaign.Step.CollectBlind"),
            CampaignPhase.AwaitingActions
                when current is not null =>
                LanguageRuntime.Format("Campaign.Step.TurnFormat", current.DisplayName),
            CampaignPhase.AwaitingActions => LanguageRuntime.GetString("Campaign.Step.CollectActions"),
            CampaignPhase.ReadyForResolution
                when campaign.GmKind == CampaignGmKind.Ai =>
                LanguageRuntime.GetString("Campaign.Step.GenerateGm"),
            CampaignPhase.ReadyForResolution => LanguageRuntime.GetString("Campaign.Step.FillGm"),
            CampaignPhase.Paused => LanguageRuntime.GetString("Campaign.Step.Paused"),
            CampaignPhase.Completed => LanguageRuntime.GetString("Campaign.Step.Completed"),
            _ => LanguageRuntime.GetString("Campaign.Step.Updating")
        };
    }

    private static string StepDescription(
        Campaign campaign,
        CampaignActionExecutionMode executionMode,
        CampaignParticipant? current,
        int failures,
        bool gmResolutionFailed,
        bool gmCandidatePending,
        bool hasUserSeat)
    {
        if (gmCandidatePending)
        {
            return LanguageRuntime.GetString("Campaign.Step.GmRetryPending");
        }

        if (gmResolutionFailed)
        {
            return LanguageRuntime.GetString("Campaign.Step.GmFailedHelp");
        }

        if (failures > 0)
        {
            return LanguageRuntime.GetString("Campaign.Step.PlayerFailedHelp");
        }

        return campaign.Phase switch
        {
            CampaignPhase.AwaitingActions
                when executionMode == CampaignActionExecutionMode.Parallel =>
                hasUserSeat
                    ? LanguageRuntime.GetString("Campaign.Step.BlindWithUser")
                    : LanguageRuntime.GetString("Campaign.Step.BlindAiOnly"),
            CampaignPhase.AwaitingActions
                when current is not null =>
                LanguageRuntime.Format("Campaign.Step.StrictFormat", current.DisplayName),
            CampaignPhase.AwaitingActions =>
                hasUserSeat
                    ? LanguageRuntime.GetString("Campaign.Step.FlexibleWithUser")
                    : LanguageRuntime.GetString("Campaign.Step.FlexibleAiOnly"),
            CampaignPhase.ReadyForResolution
                when campaign.GmKind == CampaignGmKind.Ai =>
                LanguageRuntime.GetString("Campaign.Step.AiGmConfirm"),
            CampaignPhase.ReadyForResolution =>
                LanguageRuntime.GetString("Campaign.Step.HumanGmFill"),
            _ => LanguageRuntime.GetString("Campaign.Step.WaitOrReload")
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
            return LanguageRuntime.GetString("Campaign.User.NoSeat");
        }

        if (userHasAction)
        {
            return LanguageRuntime.GetString("Campaign.User.AlreadyActed");
        }

        if (campaign.Phase != CampaignPhase.AwaitingActions)
        {
            return LanguageRuntime.GetString("Campaign.User.NotActionPhase");
        }

        if (current is not null && current.Id != userSeat.Id)
        {
            return LanguageRuntime.Format("Campaign.User.StrictTurnFormat", current.DisplayName);
        }

        return LanguageRuntime.GetString("Campaign.User.CannotAct");
    }
}
