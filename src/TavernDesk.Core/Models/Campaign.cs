namespace TavernDesk.Core.Models;

public enum CampaignStatus
{
    Draft,
    Active,
    Completed,
    Archived
}

public enum CampaignPhase
{
    Draft,
    Opening,
    AwaitingActions,
    ReadyForResolution,
    Resolving,
    Paused,
    Completed
}

public enum CampaignFlowPreset
{
    CollaborativeTable,
    BlindSubmission,
    StrictInitiative
}

public enum CampaignGmKind
{
    User,
    Ai
}

public enum CampaignParticipantKind
{
    User,
    Ai
}

public enum CampaignVisibility
{
    Public,
    Private,
    GmOnly
}

public enum CampaignEventKind
{
    System,
    GmOpening,
    PlayerIntent,
    GmResolution,
    PrivateDelivery,
    StateDelta,
    DiceRoll
}

public enum CampaignGenerationStatus
{
    None,
    Queued,
    Streaming,
    Completed,
    Interrupted,
    Failed
}

public enum CampaignEndReason
{
    None,
    Normal,
    UserStopped,
    GlobalStop,
    Timeout,
    StreamDisconnected,
    ContextLimit,
    OutputLimit,
    RepetitionDetected,
    ProviderError,
    ProcessExited,
    ProtocolViolation
}

public sealed class Campaign
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string StoryId { get; set; } = Guid.NewGuid().ToString("N");
    public string? ParentCampaignId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string WorldSetting { get; set; } = string.Empty;
    public string Rules { get; set; } = string.Empty;
    public string OpeningPrompt { get; set; } = string.Empty;
    public CampaignGmKind GmKind { get; set; } = CampaignGmKind.Ai;
    public bool UserAlsoPlayer { get; set; } = true;
    public CampaignFlowPreset FlowPreset { get; set; } =
        CampaignFlowPreset.CollaborativeTable;
    public CampaignStatus Status { get; set; } = CampaignStatus.Draft;
    public CampaignPhase Phase { get; set; } = CampaignPhase.Draft;
    public int CurrentRound { get; set; } = 1;
    public int CurrentTurnIndex { get; set; }
    public long FrozenSequenceNo { get; set; }
    public int StateVersion { get; set; }
    public string WorldSummary { get; set; } = string.Empty;
    public string UserPersonaName { get; set; } = "USER";
    public string UserPersonaDescription { get; set; } = string.Empty;
    public string GmProviderId { get; set; } = string.Empty;
    public string GmModelId { get; set; } = string.Empty;
    public int GmContextLimit { get; set; } = 32768;
    public int GmMaxOutputTokens { get; set; } = 2048;
    public double GmTemperature { get; set; } = 0.7;
    public double GmTopP { get; set; } = 1;
    public int PlayerHistoryBudget { get; set; } = 12000;
    public int GmHistoryBudget { get; set; } = 20000;
    public int ContextTokenBudget { get; set; } = 15000;
    public int MemoryUpdateIntervalRounds { get; set; } = 3;
    public int MemoryUpdatePendingTokenThreshold { get; set; } = 4000;
    public bool MemoryEnabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? StartedAt { get; set; }

    public bool IsFrozen => Status != CampaignStatus.Draft;

    public void NormalizeContextSettings()
    {
        ContextTokenBudget = Math.Clamp(ContextTokenBudget, 8_000, 200_000);
        MemoryUpdateIntervalRounds = Math.Clamp(
            MemoryUpdateIntervalRounds,
            1,
            50);
        MemoryUpdatePendingTokenThreshold = Math.Clamp(
            MemoryUpdatePendingTokenThreshold,
            1_000,
            50_000);
    }
}

public sealed class CampaignParticipant
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string CampaignId { get; init; }
    public CampaignParticipantKind Kind { get; set; }
    public int SortIndex { get; set; }
    public bool IsEnabled { get; set; } = true;
    public string? SourceCharacterId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string CharacterSnapshotJson { get; set; } = "{}";
    public string PersonaSnapshotJson { get; set; } = "{}";
    public string MemorySnapshot { get; set; } = string.Empty;
    public string OriginalWorldKnowledgeSnapshot { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public int ContextLimit { get; set; } = 32768;
    public int MaxOutputTokens { get; set; } = 1536;
    public double Temperature { get; set; } = 0.8;
    public double TopP { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class CampaignEvent
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string CampaignId { get; init; }
    public long SequenceNo { get; set; }
    public int RoundNo { get; set; }
    public CampaignEventKind Kind { get; set; }
    public string ActorId { get; set; } = string.Empty;
    public string? RecipientId { get; set; }
    public CampaignVisibility Visibility { get; set; } = CampaignVisibility.Public;
    public string Content { get; set; } = string.Empty;
    public string StructuredDataJson { get; set; } = "{}";
    public long SnapshotSequenceNo { get; set; }
    public int AttemptNo { get; set; }
    public CampaignGenerationStatus GenerationStatus { get; set; }
    public CampaignEndReason EndReason { get; set; }
    public string OperationId { get; set; } = Guid.NewGuid().ToString("N");
    public string? ReplacesEventId { get; set; }
    public bool IsLocked { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed record CampaignSummary(
    string Id,
    string StoryId,
    string? ParentCampaignId,
    string Title,
    CampaignStatus Status,
    CampaignPhase Phase,
    CampaignFlowPreset FlowPreset,
    int CurrentRound,
    int ParticipantCount,
    DateTimeOffset UpdatedAt);

public sealed record CampaignAggregate(
    Campaign Campaign,
    IReadOnlyList<CampaignParticipant> Participants,
    IReadOnlyList<CampaignEvent> Events);

/// <summary>
/// Resolves the action slot that the current GM request is adjudicating.
/// <para>
/// A strict-initiative round contains one slot per enabled participant, while
/// collaborative and blind-submission flows use the latest locked action in
/// the current round as their single slot.  The slot is intentionally derived
/// from existing event data so no persistence/schema change is required.
/// </para>
/// </summary>
public static class CampaignResolutionScope
{
    public static CampaignEvent? FindCurrentAction(CampaignAggregate aggregate)
    {
        ArgumentNullException.ThrowIfNull(aggregate);

        var actions = aggregate.Events
            .Where(item =>
                item.RoundNo == aggregate.Campaign.CurrentRound
                && item.Kind == CampaignEventKind.PlayerIntent
                && item.GenerationStatus == CampaignGenerationStatus.Completed
                && item.IsLocked)
            .OrderByDescending(item => item.SequenceNo);

        if (aggregate.Campaign.FlowPreset != CampaignFlowPreset.StrictInitiative)
        {
            return actions.FirstOrDefault();
        }

        var enabled = aggregate.Participants
            .Where(item => item.IsEnabled)
            .OrderBy(item => item.SortIndex)
            .ToArray();
        if (enabled.Length == 0)
        {
            return null;
        }

        var currentParticipant =
            enabled[aggregate.Campaign.CurrentTurnIndex % enabled.Length];
        return actions.FirstOrDefault(item =>
            string.Equals(
                item.ActorId,
                currentParticipant.Id,
                StringComparison.Ordinal));
    }

    public static IReadOnlyList<CampaignEvent> GetCurrentGmResolutions(
        CampaignAggregate aggregate)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        var action = FindCurrentAction(aggregate);
        if (action is null)
        {
            return Array.Empty<CampaignEvent>();
        }

        var resolutions = aggregate.Events
            .Where(item =>
                item.RoundNo == aggregate.Campaign.CurrentRound
                && item.Kind == CampaignEventKind.GmResolution)
            .OrderBy(item => item.SequenceNo)
            .ToArray();
        var includedIds = new HashSet<string>(StringComparer.Ordinal);
        var includedSequences = new HashSet<long> { action.SequenceNo };

        // New attempts keep the action sequence as their snapshot.  The
        // chained checks also keep retries from older data readable when they
        // pointed at the previous failed GM event instead.
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

    public static bool IsCurrentGmResolution(
        CampaignAggregate aggregate,
        CampaignEvent campaignEvent) =>
        GetCurrentGmResolutions(aggregate)
            .Any(item => string.Equals(item.Id, campaignEvent.Id, StringComparison.Ordinal));
}

public sealed record CampaignRuntimeUpdate(
    CampaignPhase Phase,
    int CurrentRound,
    int CurrentTurnIndex,
    long FrozenSequenceNo,
    string WorldSummary,
    bool MarkCompleted = false,
    bool ActivatePendingUser = false,
    string? CommitEventId = null);

public sealed record CampaignContextSettingsUpdate(
    int PlayerHistoryBudget,
    int GmHistoryBudget,
    int ContextTokenBudget,
    int MemoryUpdateIntervalRounds,
    int MemoryUpdatePendingTokenThreshold);

public sealed record CampaignModelRoute(
    string ProviderId,
    string ModelId,
    int ContextLimit,
    int MaxOutputTokens,
    double Temperature,
    double TopP);
