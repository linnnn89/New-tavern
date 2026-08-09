namespace TavernDesk.Core.Models;

/// <summary>
/// Semantic stage of the active campaign flow.  This is deliberately separate
/// from <see cref="CampaignPhase"/>: the latter is persisted campaign state,
/// while this enum describes the strategy's actionable projection for the UI
/// and command layer.
/// </summary>
public enum CampaignFlowStage
{
    Draft,
    Opening,
    AwaitingActions,
    RetryingPlayerAction,
    ReadyForResolution,
    RetryingResolution,
    SelectingResolutionCandidate,
    Paused,
    Completed
}

public enum CampaignActionExecutionMode
{
    Single,
    Sequential,
    Parallel
}

/// <summary>
/// A stable, non-localized reason returned by a flow strategy.  UI text and
/// provider/context errors remain outside the core flow model.
/// </summary>
public enum CampaignFlowBlockReason
{
    None,
    CampaignNotActive,
    WrongPhase,
    NoEnabledParticipants,
    AwaitingOtherActions,
    CurrentParticipantUnavailable,
    NoCurrentAction,
    ActionAlreadyAttempted,
    GenerationInProgress,
    FailedAttemptRequiresRetry,
    NoValidCandidate
}

public sealed record CampaignActionPlan(
    IReadOnlyList<string> AllowedParticipantIds,
    CampaignActionExecutionMode ExecutionMode,
    CampaignVisibility PendingVisibility,
    bool CanSubmit,
    CampaignFlowBlockReason BlockReason);

public sealed record CampaignAdvancePlan(
    CampaignPhase NextPhase,
    int NextRound,
    int NextTurnIndex,
    bool ActivatePendingUser,
    bool CommitCandidate);

/// <summary>
/// Identifies exactly which events belong to one GM operation.  Consumers must
/// use these IDs instead of rescanning the aggregate with their own mode rules.
/// </summary>
public sealed record CampaignResolutionPlan(
    long? AnchorSequenceNo,
    IReadOnlyList<string> PlayerIntentIds,
    IReadOnlyList<string> CandidateResolutionIds,
    bool CanGenerate,
    bool CanCommit,
    CampaignFlowBlockReason BlockReason);

/// <summary>
/// One authoritative strategy projection used by the UI, runner and context
/// planner.  It contains facts and IDs only; localized labels and token
/// estimates belong to their respective layers.
/// </summary>
public sealed record CampaignFlowSnapshot(
    string CampaignId,
    int StateVersion,
    CampaignFlowPreset Preset,
    CampaignFlowStage Stage,
    string? CurrentParticipantId,
    IReadOnlyList<string> EnabledParticipantIds,
    IReadOnlyList<string> CompletedParticipantIds,
    CampaignActionPlan ActionPlan,
    CampaignResolutionPlan ResolutionPlan,
    int RequiredParticipantCount,
    CampaignAdvancePlan? NextStep);
