using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;

namespace TavernDesk.Core.Flow.Strategies;

/// <summary>
/// Pure per-seat state machine for strict initiative.  One locked player
/// action and its GM retry chain form the only current resolution slot.
/// </summary>
public sealed class StrictInitiativeStrategy : ICampaignFlowStrategy
{
    public CampaignFlowPreset Preset => CampaignFlowPreset.StrictInitiative;

    public CampaignFlowSnapshot Inspect(CampaignAggregate aggregate)
    {
        EnsurePreset(aggregate);
        var enabled = EnabledParticipants(aggregate);
        var current = CurrentParticipant(aggregate, enabled);
        var actionPlan = BuildActionPlan(
            aggregate,
            enabled,
            current,
            requestedParticipantId: null);
        var resolutionPlan = BuildResolutionPlan(
            aggregate,
            enabled,
            current);
        var completedIds = enabled
            .Where(participant => HasCompletedAction(
                aggregate,
                participant.Id))
            .Select(participant => participant.Id)
            .ToArray();
        var stage = DetermineStage(
            aggregate,
            current,
            resolutionPlan);
        var nextStep = current is not null
                       && (resolutionPlan.CanGenerate
                           || resolutionPlan.CanCommit)
            ? CreateAdvancePlan(
                aggregate,
                enabled,
                commitCandidate: resolutionPlan.CanCommit)
            : null;

        return new CampaignFlowSnapshot(
            aggregate.Campaign.Id,
            aggregate.Campaign.StateVersion,
            Preset,
            stage,
            current?.Id,
            enabled.Select(item => item.Id).ToArray(),
            completedIds,
            actionPlan,
            resolutionPlan,
            enabled.Length,
            nextStep);
    }

    public CampaignActionPlan PlanAction(
        CampaignAggregate aggregate,
        string participantId)
    {
        EnsurePreset(aggregate);
        ArgumentException.ThrowIfNullOrWhiteSpace(participantId);
        var enabled = EnabledParticipants(aggregate);
        return BuildActionPlan(
            aggregate,
            enabled,
            CurrentParticipant(aggregate, enabled),
            participantId);
    }

    public CampaignResolutionPlan PlanResolution(
        CampaignAggregate aggregate)
    {
        EnsurePreset(aggregate);
        var enabled = EnabledParticipants(aggregate);
        return BuildResolutionPlan(
            aggregate,
            enabled,
            CurrentParticipant(aggregate, enabled));
    }

    public CampaignAdvancePlan PlanAdvance(
        CampaignAggregate aggregate,
        CampaignEvent resolution,
        bool commitCandidate)
    {
        EnsurePreset(aggregate);
        ArgumentNullException.ThrowIfNull(resolution);
        var enabled = EnabledParticipants(aggregate);
        if (aggregate.Campaign.Status != CampaignStatus.Active
            || aggregate.Campaign.Phase != CampaignPhase.ReadyForResolution)
        {
            throw new InvalidOperationException(
                "Strict initiative can only advance from the resolution phase.");
        }

        var current = CurrentParticipant(aggregate, enabled)
                      ?? throw new InvalidOperationException(
                          "Strict initiative has no enabled current participant.");
        var action = CurrentAction(aggregate, current.Id)
                     ?? throw new InvalidOperationException(
                         "Strict initiative has no locked action to resolve.");
        if (resolution.CampaignId != aggregate.Campaign.Id
            || resolution.RoundNo != aggregate.Campaign.CurrentRound
            || resolution.Kind != CampaignEventKind.GmResolution
            || resolution.GenerationStatus
               != CampaignGenerationStatus.Completed
            || resolution.EndReason != CampaignEndReason.Normal
            || !BelongsToCurrentSlot(aggregate, action, resolution))
        {
            throw new InvalidOperationException(
                "The GM resolution does not belong to the current strict-initiative slot.");
        }

        return CreateAdvancePlan(
            aggregate,
            enabled,
            commitCandidate);
    }

    public bool IsEventVisibleToParticipant(
        CampaignAggregate aggregate,
        CampaignEvent campaignEvent,
        CampaignParticipant participant)
    {
        EnsurePreset(aggregate);
        ArgumentNullException.ThrowIfNull(campaignEvent);
        ArgumentNullException.ThrowIfNull(participant);
        return campaignEvent.Visibility == CampaignVisibility.Public
               || string.Equals(campaignEvent.ActorId, participant.Id, StringComparison.Ordinal)
               || string.Equals(campaignEvent.RecipientId, participant.Id, StringComparison.Ordinal);
    }

    public bool IsEventVisibleToObserver(
        CampaignAggregate aggregate,
        CampaignEvent campaignEvent)
    {
        EnsurePreset(aggregate);
        ArgumentNullException.ThrowIfNull(campaignEvent);
        return campaignEvent.Visibility == CampaignVisibility.Public;
    }

    private static CampaignActionPlan BuildActionPlan(
        CampaignAggregate aggregate,
        IReadOnlyList<CampaignParticipant> enabled,
        CampaignParticipant? current,
        string? requestedParticipantId)
    {
        if (aggregate.Campaign.Status != CampaignStatus.Active)
        {
            return BlockedAction(CampaignFlowBlockReason.CampaignNotActive);
        }

        if (enabled.Count == 0 || current is null)
        {
            return BlockedAction(CampaignFlowBlockReason.NoEnabledParticipants);
        }

        if (aggregate.Campaign.Phase != CampaignPhase.AwaitingActions)
        {
            return BlockedAction(CampaignFlowBlockReason.WrongPhase, current.Id);
        }

        if (!string.IsNullOrWhiteSpace(requestedParticipantId)
            && !string.Equals(
                requestedParticipantId,
                current.Id,
                StringComparison.Ordinal))
        {
            return BlockedAction(
                CampaignFlowBlockReason.CurrentParticipantUnavailable,
                current.Id);
        }

        var latestAttempt = LatestActionAttempt(aggregate, current.Id);
        if (latestAttempt is null)
        {
            return new CampaignActionPlan(
                [current.Id],
                CampaignActionExecutionMode.Single,
                CampaignVisibility.Public,
                CanSubmit: true,
                BlockReason: CampaignFlowBlockReason.None);
        }

        if (latestAttempt.GenerationStatus is
            CampaignGenerationStatus.Failed
            or CampaignGenerationStatus.Interrupted)
        {
            return BlockedAction(
                CampaignFlowBlockReason.FailedAttemptRequiresRetry,
                current.Id);
        }

        if (latestAttempt.GenerationStatus is
            CampaignGenerationStatus.Queued
            or CampaignGenerationStatus.Streaming)
        {
            return BlockedAction(
                CampaignFlowBlockReason.GenerationInProgress,
                current.Id);
        }

        return BlockedAction(
            CampaignFlowBlockReason.ActionAlreadyAttempted,
            current.Id);
    }

    private static CampaignResolutionPlan BuildResolutionPlan(
        CampaignAggregate aggregate,
        IReadOnlyList<CampaignParticipant> enabled,
        CampaignParticipant? current)
    {
        if (aggregate.Campaign.Status != CampaignStatus.Active)
        {
            return BlockedResolution(
                CampaignFlowBlockReason.CampaignNotActive);
        }

        if (enabled.Count == 0 || current is null)
        {
            return BlockedResolution(
                CampaignFlowBlockReason.NoEnabledParticipants);
        }

        if (aggregate.Campaign.Phase != CampaignPhase.ReadyForResolution)
        {
            return BlockedResolution(CampaignFlowBlockReason.WrongPhase);
        }

        var action = CurrentAction(aggregate, current.Id);
        if (action is null)
        {
            return BlockedResolution(CampaignFlowBlockReason.NoCurrentAction);
        }

        var candidates = CurrentResolutionCandidates(aggregate, action);
        var candidateIds = candidates.Select(item => item.Id).ToArray();
        var validCandidates = candidates
            .Where(IsValidResolutionCandidate)
            .ToArray();
        if (validCandidates.Length > 0)
        {
            return new CampaignResolutionPlan(
                action.SequenceNo,
                [action.Id],
                candidateIds,
                CanGenerate: false,
                CanCommit: true,
                BlockReason: CampaignFlowBlockReason.None);
        }

        var latest = candidates.LastOrDefault();
        if (latest?.GenerationStatus is
            CampaignGenerationStatus.Queued
            or CampaignGenerationStatus.Streaming)
        {
            return new CampaignResolutionPlan(
                action.SequenceNo,
                [action.Id],
                candidateIds,
                CanGenerate: false,
                CanCommit: false,
                BlockReason: CampaignFlowBlockReason.GenerationInProgress);
        }

        if (latest is null
            || latest.GenerationStatus is
                CampaignGenerationStatus.Failed
                or CampaignGenerationStatus.Interrupted
            || latest.EndReason != CampaignEndReason.Normal)
        {
            return new CampaignResolutionPlan(
                action.SequenceNo,
                [action.Id],
                candidateIds,
                CanGenerate: true,
                CanCommit: false,
                BlockReason: CampaignFlowBlockReason.None);
        }

        return new CampaignResolutionPlan(
            action.SequenceNo,
            [action.Id],
            candidateIds,
            CanGenerate: false,
            CanCommit: false,
            BlockReason: CampaignFlowBlockReason.NoValidCandidate);
    }

    private static CampaignFlowStage DetermineStage(
        CampaignAggregate aggregate,
        CampaignParticipant? current,
        CampaignResolutionPlan resolutionPlan)
    {
        if (aggregate.Campaign.Status == CampaignStatus.Draft
            || aggregate.Campaign.Phase == CampaignPhase.Draft)
        {
            return CampaignFlowStage.Draft;
        }

        if (aggregate.Campaign.Phase == CampaignPhase.Opening)
        {
            return CampaignFlowStage.Opening;
        }

        if (aggregate.Campaign.Phase == CampaignPhase.Paused)
        {
            return CampaignFlowStage.Paused;
        }

        if (aggregate.Campaign.Status is
            CampaignStatus.Completed or CampaignStatus.Archived
            || aggregate.Campaign.Phase == CampaignPhase.Completed)
        {
            return CampaignFlowStage.Completed;
        }

        if (aggregate.Campaign.Phase == CampaignPhase.AwaitingActions)
        {
            var latest = current is null
                ? null
                : LatestActionAttempt(aggregate, current.Id);
            if (latest?.GenerationStatus == CampaignGenerationStatus.Completed
                && latest.IsLocked)
            {
                return CampaignFlowStage.ReadyForResolution;
            }

            return latest?.GenerationStatus is
                CampaignGenerationStatus.Failed
                or CampaignGenerationStatus.Interrupted
                ? CampaignFlowStage.RetryingPlayerAction
                : CampaignFlowStage.AwaitingActions;
        }

        if (aggregate.Campaign.Phase == CampaignPhase.ReadyForResolution)
        {
            if (resolutionPlan.CanCommit)
            {
                return CampaignFlowStage.SelectingResolutionCandidate;
            }

            if (resolutionPlan.BlockReason
                == CampaignFlowBlockReason.GenerationInProgress)
            {
                return CampaignFlowStage.ReadyForResolution;
            }

            return resolutionPlan.CandidateResolutionIds.Count > 0
                ? CampaignFlowStage.RetryingResolution
                : CampaignFlowStage.ReadyForResolution;
        }

        return CampaignFlowStage.ReadyForResolution;
    }

    private static CampaignAdvancePlan CreateAdvancePlan(
        CampaignAggregate aggregate,
        IReadOnlyList<CampaignParticipant> enabled,
        bool commitCandidate)
    {
        if (enabled.Count == 0)
        {
            throw new InvalidOperationException(
                "Strict initiative cannot advance without enabled participants.");
        }

        var currentIndex = NormalizeTurnIndex(
            aggregate.Campaign.CurrentTurnIndex,
            enabled.Count);
        var nextTurn = currentIndex + 1;
        var nextRound = aggregate.Campaign.CurrentRound;
        var activatePendingUser = false;
        if (nextTurn >= enabled.Count)
        {
            nextTurn = 0;
            nextRound++;
            activatePendingUser = true;
        }

        return new CampaignAdvancePlan(
            CampaignPhase.AwaitingActions,
            nextRound,
            nextTurn,
            activatePendingUser,
            commitCandidate);
    }

    private static CampaignParticipant[] EnabledParticipants(
        CampaignAggregate aggregate) =>
        aggregate.Participants
            .Where(item => item.IsEnabled)
            .OrderBy(item => item.SortIndex)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();

    private static CampaignParticipant? CurrentParticipant(
        CampaignAggregate aggregate,
        IReadOnlyList<CampaignParticipant> enabled) =>
        enabled.Count == 0
            ? null
            : enabled[NormalizeTurnIndex(
                aggregate.Campaign.CurrentTurnIndex,
                enabled.Count)];

    private static int NormalizeTurnIndex(int index, int count)
    {
        var normalized = index % count;
        return normalized < 0 ? normalized + count : normalized;
    }

    private static CampaignEvent? LatestActionAttempt(
        CampaignAggregate aggregate,
        string participantId) =>
        aggregate.Events
            .Where(item =>
                item.RoundNo == aggregate.Campaign.CurrentRound
                && item.Kind == CampaignEventKind.PlayerIntent
                && string.Equals(
                    item.ActorId,
                    participantId,
                    StringComparison.Ordinal))
            .OrderBy(item => item.SequenceNo)
            .LastOrDefault();

    private static CampaignEvent? CurrentAction(
        CampaignAggregate aggregate,
        string participantId) =>
        aggregate.Events
            .Where(item =>
                item.RoundNo == aggregate.Campaign.CurrentRound
                && item.Kind == CampaignEventKind.PlayerIntent
                && string.Equals(
                    item.ActorId,
                    participantId,
                    StringComparison.Ordinal)
                && item.GenerationStatus
                   == CampaignGenerationStatus.Completed
                && item.IsLocked)
            .OrderBy(item => item.SequenceNo)
            .LastOrDefault();

    private static bool HasCompletedAction(
        CampaignAggregate aggregate,
        string participantId) =>
        CurrentAction(aggregate, participantId) is not null;

    private static IReadOnlyList<CampaignEvent> CurrentResolutionCandidates(
        CampaignAggregate aggregate,
        CampaignEvent action)
    {
        var resolutions = aggregate.Events
            .Where(item =>
                item.RoundNo == aggregate.Campaign.CurrentRound
                && item.Kind == CampaignEventKind.GmResolution)
            .OrderBy(item => item.SequenceNo)
            .ToArray();
        var includedIds = new HashSet<string>(StringComparer.Ordinal);
        var includedSequences = new HashSet<long> { action.SequenceNo };
        foreach (var resolution in resolutions)
        {
            if (!includedSequences.Contains(resolution.SnapshotSequenceNo)
                && (string.IsNullOrWhiteSpace(resolution.ReplacesEventId)
                    || !includedIds.Contains(resolution.ReplacesEventId)))
            {
                continue;
            }

            includedIds.Add(resolution.Id);
            includedSequences.Add(resolution.SequenceNo);
        }

        return resolutions
            .Where(item => includedIds.Contains(item.Id))
            .ToArray();
    }

    private static bool BelongsToCurrentSlot(
        CampaignAggregate aggregate,
        CampaignEvent action,
        CampaignEvent resolution) =>
        resolution.SnapshotSequenceNo == action.SequenceNo
        || CurrentResolutionCandidates(aggregate, action)
            .Any(item => string.Equals(
                item.Id,
                resolution.Id,
                StringComparison.Ordinal));

    private static bool IsValidResolutionCandidate(CampaignEvent candidate) =>
        candidate.GenerationStatus == CampaignGenerationStatus.Completed
        && candidate.EndReason == CampaignEndReason.Normal;

    private static CampaignActionPlan BlockedAction(
        CampaignFlowBlockReason reason,
        string? currentParticipantId = null) =>
        new(
            string.IsNullOrWhiteSpace(currentParticipantId)
                ? []
                : [currentParticipantId],
            CampaignActionExecutionMode.Single,
            CampaignVisibility.Public,
            CanSubmit: false,
            BlockReason: reason);

    private static CampaignResolutionPlan BlockedResolution(
        CampaignFlowBlockReason reason) =>
        new(
            null,
            [],
            [],
            CanGenerate: false,
            CanCommit: false,
            BlockReason: reason);

    private static void EnsurePreset(CampaignAggregate aggregate)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        if (aggregate.Campaign.FlowPreset
            != CampaignFlowPreset.StrictInitiative)
        {
            throw new ArgumentException(
                "StrictInitiativeStrategy can only inspect strict-initiative campaigns.",
                nameof(aggregate));
        }
    }
}
