using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;

namespace TavernDesk.Core.Flow.Strategies;

/// <summary>
/// Shared all-seats-then-GM cadence. Concrete strategies own visibility and
/// action execution policy while sharing the batch round state machine.
/// </summary>
public abstract class BatchCampaignFlowStrategy : ICampaignFlowStrategy
{
    public abstract CampaignFlowPreset Preset { get; }
    protected abstract CampaignActionExecutionMode ExecutionMode { get; }
    protected abstract CampaignVisibility PendingVisibility { get; }

    public CampaignFlowSnapshot Inspect(CampaignAggregate aggregate)
    {
        EnsurePreset(aggregate);
        var enabled = Enabled(aggregate);
        var actionPlan = BuildActionPlan(aggregate, enabled, null);
        var resolutionPlan = BuildResolutionPlan(aggregate, enabled);
        var completed = enabled.Where(p => CompletedAction(aggregate, p.Id) is not null)
            .Select(p => p.Id).ToArray();
        var stage = DetermineStage(aggregate, enabled, resolutionPlan);
        var next = resolutionPlan.CanGenerate || resolutionPlan.CanCommit
            ? new CampaignAdvancePlan(CampaignPhase.AwaitingActions,
                aggregate.Campaign.CurrentRound + 1, 0, true,
                resolutionPlan.CanCommit)
            : null;
        return new CampaignFlowSnapshot(
            aggregate.Campaign.Id, aggregate.Campaign.StateVersion, Preset,
            stage, null, enabled.Select(p => p.Id).ToArray(), completed,
            actionPlan, resolutionPlan, enabled.Length, next);
    }

    public CampaignActionPlan PlanAction(CampaignAggregate aggregate, string participantId)
    {
        EnsurePreset(aggregate);
        ArgumentException.ThrowIfNullOrWhiteSpace(participantId);
        return BuildActionPlan(aggregate, Enabled(aggregate), participantId);
    }

    public CampaignResolutionPlan PlanResolution(CampaignAggregate aggregate)
    {
        EnsurePreset(aggregate);
        return BuildResolutionPlan(aggregate, Enabled(aggregate));
    }

    public CampaignAdvancePlan PlanAdvance(
        CampaignAggregate aggregate, CampaignEvent resolution, bool commitCandidate)
    {
        EnsurePreset(aggregate);
        ArgumentNullException.ThrowIfNull(resolution);
        var plan = BuildResolutionPlan(aggregate, Enabled(aggregate));
        if (!plan.CandidateResolutionIds.Contains(resolution.Id, StringComparer.Ordinal)
            || resolution.GenerationStatus != CampaignGenerationStatus.Completed
            || resolution.EndReason != CampaignEndReason.Normal)
        {
            throw new InvalidOperationException("The GM resolution does not belong to the current batch round.");
        }

        return new CampaignAdvancePlan(CampaignPhase.AwaitingActions,
            aggregate.Campaign.CurrentRound + 1, 0, true, commitCandidate);
    }

    public abstract bool IsEventVisibleToParticipant(
        CampaignAggregate aggregate, CampaignEvent campaignEvent,
        CampaignParticipant participant);

    public abstract bool IsEventVisibleToObserver(
        CampaignAggregate aggregate,
        CampaignEvent campaignEvent);

    private CampaignActionPlan BuildActionPlan(
        CampaignAggregate aggregate, IReadOnlyList<CampaignParticipant> enabled,
        string? requestedId)
    {
        if (aggregate.Campaign.Status != CampaignStatus.Active)
            return Blocked(CampaignFlowBlockReason.CampaignNotActive);
        if (enabled.Count == 0)
            return Blocked(CampaignFlowBlockReason.NoEnabledParticipants);
        if (aggregate.Campaign.Phase != CampaignPhase.AwaitingActions)
            return Blocked(CampaignFlowBlockReason.WrongPhase);

        var missing = enabled.Where(p => LatestAttempt(aggregate, p.Id) is null)
            .Select(p => p.Id).ToArray();
        if (requestedId is not null)
        {
            if (!enabled.Any(p => string.Equals(p.Id, requestedId, StringComparison.Ordinal)))
                return Blocked(CampaignFlowBlockReason.CurrentParticipantUnavailable);
            var latest = LatestAttempt(aggregate, requestedId);
            if (latest?.GenerationStatus is CampaignGenerationStatus.Failed or CampaignGenerationStatus.Interrupted)
                return Blocked(CampaignFlowBlockReason.FailedAttemptRequiresRetry, [requestedId]);
            if (latest?.GenerationStatus is CampaignGenerationStatus.Queued or CampaignGenerationStatus.Streaming)
                return Blocked(CampaignFlowBlockReason.GenerationInProgress, [requestedId]);
            if (latest is not null)
                return Blocked(CampaignFlowBlockReason.ActionAlreadyAttempted, [requestedId]);
            missing = [requestedId];
        }

        if (missing.Length == 0)
            return Blocked(CampaignFlowBlockReason.ActionAlreadyAttempted);
        return new CampaignActionPlan(missing, ExecutionMode, PendingVisibility,
            true, CampaignFlowBlockReason.None);
    }

    private CampaignResolutionPlan BuildResolutionPlan(
        CampaignAggregate aggregate, IReadOnlyList<CampaignParticipant> enabled)
    {
        if (aggregate.Campaign.Status != CampaignStatus.Active)
            return BlockedResolution(CampaignFlowBlockReason.CampaignNotActive);
        if (enabled.Count == 0)
            return BlockedResolution(CampaignFlowBlockReason.NoEnabledParticipants);

        var actions = enabled.Select(p => CompletedAction(aggregate, p.Id)).ToArray();
        if (actions.Any(a => a is null))
            return BlockedResolution(CampaignFlowBlockReason.AwaitingOtherActions);
        if (aggregate.Campaign.Phase is not (CampaignPhase.AwaitingActions or CampaignPhase.ReadyForResolution))
            return BlockedResolution(CampaignFlowBlockReason.WrongPhase);

        var locked = actions.Select(a => a!).OrderBy(a => a.SequenceNo).ToArray();
        var anchor = locked[^1];
        var candidates = ResolutionCandidates(aggregate, anchor);
        var valid = candidates.Any(IsValidResolution);
        var latest = candidates.LastOrDefault();
        if (valid)
            return new CampaignResolutionPlan(anchor.SequenceNo, locked.Select(a => a.Id).ToArray(),
                candidates.Select(c => c.Id).ToArray(), false, true, CampaignFlowBlockReason.None);
        if (latest?.GenerationStatus is CampaignGenerationStatus.Queued or CampaignGenerationStatus.Streaming)
            return new CampaignResolutionPlan(anchor.SequenceNo, locked.Select(a => a.Id).ToArray(),
                candidates.Select(c => c.Id).ToArray(), false, false, CampaignFlowBlockReason.GenerationInProgress);
        return new CampaignResolutionPlan(anchor.SequenceNo, locked.Select(a => a.Id).ToArray(),
            candidates.Select(c => c.Id).ToArray(), true, false, CampaignFlowBlockReason.None);
    }

    private static CampaignFlowStage DetermineStage(
        CampaignAggregate aggregate, IReadOnlyList<CampaignParticipant> enabled,
        CampaignResolutionPlan resolution)
    {
        if (aggregate.Campaign.Status == CampaignStatus.Draft || aggregate.Campaign.Phase == CampaignPhase.Draft)
            return CampaignFlowStage.Draft;
        if (aggregate.Campaign.Phase == CampaignPhase.Opening) return CampaignFlowStage.Opening;
        if (aggregate.Campaign.Phase == CampaignPhase.Paused) return CampaignFlowStage.Paused;
        if (aggregate.Campaign.Status is CampaignStatus.Completed or CampaignStatus.Archived
            || aggregate.Campaign.Phase == CampaignPhase.Completed) return CampaignFlowStage.Completed;
        if (resolution.CanCommit) return CampaignFlowStage.SelectingResolutionCandidate;
        if (resolution.CanGenerate)
            return resolution.CandidateResolutionIds.Count > 0
                ? CampaignFlowStage.RetryingResolution : CampaignFlowStage.ReadyForResolution;
        if (resolution.BlockReason == CampaignFlowBlockReason.GenerationInProgress)
            return CampaignFlowStage.ReadyForResolution;
        var failed = enabled.Any(p => LatestAttempt(aggregate, p.Id)?.GenerationStatus
            is CampaignGenerationStatus.Failed or CampaignGenerationStatus.Interrupted);
        return failed ? CampaignFlowStage.RetryingPlayerAction : CampaignFlowStage.AwaitingActions;
    }

    private static CampaignParticipant[] Enabled(CampaignAggregate aggregate) => aggregate.Participants
        .Where(p => p.IsEnabled).OrderBy(p => p.SortIndex).ThenBy(p => p.Id, StringComparer.Ordinal).ToArray();
    private static CampaignEvent? LatestAttempt(CampaignAggregate a, string id) => a.Events
        .Where(e => e.RoundNo == a.Campaign.CurrentRound && e.Kind == CampaignEventKind.PlayerIntent
                    && string.Equals(e.ActorId, id, StringComparison.Ordinal))
        .OrderBy(e => e.SequenceNo).LastOrDefault();
    private static CampaignEvent? CompletedAction(CampaignAggregate a, string id) => a.Events
        .Where(e => e.RoundNo == a.Campaign.CurrentRound && e.Kind == CampaignEventKind.PlayerIntent
                    && string.Equals(e.ActorId, id, StringComparison.Ordinal)
                    && e.GenerationStatus == CampaignGenerationStatus.Completed && e.IsLocked)
        .OrderBy(e => e.SequenceNo).LastOrDefault();
    private static IReadOnlyList<CampaignEvent> ResolutionCandidates(CampaignAggregate a, CampaignEvent anchor)
    {
        var all = a.Events.Where(e => e.RoundNo == a.Campaign.CurrentRound && e.Kind == CampaignEventKind.GmResolution)
            .OrderBy(e => e.SequenceNo).ToArray();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var seq = new HashSet<long> { anchor.SequenceNo };
        foreach (var e in all)
        {
            if (!seq.Contains(e.SnapshotSequenceNo)
                && (string.IsNullOrWhiteSpace(e.ReplacesEventId) || !ids.Contains(e.ReplacesEventId))) continue;
            ids.Add(e.Id); seq.Add(e.SequenceNo);
        }
        return all.Where(e => ids.Contains(e.Id)).ToArray();
    }
    private static bool IsValidResolution(CampaignEvent e) => e.GenerationStatus == CampaignGenerationStatus.Completed
        && e.EndReason == CampaignEndReason.Normal && !string.IsNullOrWhiteSpace(e.Content);
    private CampaignActionPlan Blocked(CampaignFlowBlockReason reason, IReadOnlyList<string>? ids = null) =>
        new(ids ?? Array.Empty<string>(), ExecutionMode, PendingVisibility, false, reason);
    private static CampaignResolutionPlan BlockedResolution(CampaignFlowBlockReason reason) =>
        new(null, Array.Empty<string>(), Array.Empty<string>(), false, false, reason);
    private void EnsurePreset(CampaignAggregate aggregate)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        if (aggregate.Campaign.FlowPreset != Preset)
            throw new InvalidOperationException($"{GetType().Name} cannot handle {aggregate.Campaign.FlowPreset}.");
    }
}
