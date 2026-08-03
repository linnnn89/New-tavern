using TavernDesk.Core.Models;

namespace TavernDesk.Core.Abstractions;

public interface ICampaignScenarioRepository
{
    Task<IReadOnlyList<CampaignScenario>> ListAsync(
        CancellationToken cancellationToken = default);

    Task<CampaignScenario?> GetAsync(
        string scenarioId,
        CancellationToken cancellationToken = default);

    Task UpsertAsync(
        CampaignScenario scenario,
        CancellationToken cancellationToken = default);
}

public interface ICampaignScenarioCardImporter
{
    Task<CampaignScenarioImportResult> ImportAsync(
        string sourcePath,
        CancellationToken cancellationToken = default);
}

public sealed record CampaignCharacterSnapshotResult(
    string CharacterSnapshotJson,
    string MemorySnapshot,
    string OriginalWorldKnowledgeSnapshot,
    IReadOnlyList<string> Warnings);

public interface ICampaignCharacterSnapshotAdapter
{
    CampaignCharacterSnapshotResult Create(
        Character character,
        string? memoryBody,
        bool includeMemory,
        bool includeOriginalWorldKnowledge);
}

public interface ICampaignRepository
{
    Task<IReadOnlyList<CampaignSummary>> ListAsync(
        bool includeArchived = false,
        CancellationToken cancellationToken = default);

    Task<CampaignAggregate?> GetAsync(
        string campaignId,
        CancellationToken cancellationToken = default);

    Task SaveDraftAsync(
        Campaign campaign,
        IReadOnlyList<CampaignParticipant> participants,
        CancellationToken cancellationToken = default);

    Task<CampaignAggregate> StartAsync(
        string campaignId,
        CancellationToken cancellationToken = default);

    Task<CampaignAggregate> CloneAsDraftAsync(
        string campaignId,
        CancellationToken cancellationToken = default);

    Task<CampaignEvent> AppendEventAsync(
        CampaignEvent campaignEvent,
        CancellationToken cancellationToken = default);

    Task UpdateEventAsync(
        CampaignEvent campaignEvent,
        CancellationToken cancellationToken = default);

    Task UpdateRuntimeAsync(
        string campaignId,
        int expectedStateVersion,
        CampaignRuntimeUpdate update,
        CancellationToken cancellationToken = default);

    Task ScheduleUserJoinAsync(
        string campaignId,
        int expectedStateVersion,
        string displayName,
        string personaSnapshotJson,
        CancellationToken cancellationToken = default);

    Task UpdateParticipantRouteAsync(
        string campaignId,
        string participantId,
        CampaignModelRoute route,
        CancellationToken cancellationToken = default);

    Task UpdateGmRouteAsync(
        string campaignId,
        CampaignModelRoute route,
        CancellationToken cancellationToken = default);

    Task ArchiveAsync(
        string campaignId,
        CancellationToken cancellationToken = default);
}

public interface ICampaignRunner
{
    Task<CampaignAggregate> StartAsync(
        string campaignId,
        CancellationToken cancellationToken = default);

    Task<CampaignEvent> SubmitUserActionAsync(
        string campaignId,
        string content,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CampaignEvent>> GenerateAiActionsAsync(
        string campaignId,
        CancellationToken cancellationToken = default);

    Task<CampaignEvent> GenerateAiActionAsync(
        string campaignId,
        string participantId,
        CancellationToken cancellationToken = default);

    Task<CampaignEvent> RetryAiActionAsync(
        string campaignId,
        string failedEventId,
        CancellationToken cancellationToken = default);

    Task<CampaignEvent> SubmitUserGmResolutionAsync(
        string campaignId,
        string content,
        CancellationToken cancellationToken = default);

    Task<CampaignEvent> GenerateGmResolutionAsync(
        string campaignId,
        CancellationToken cancellationToken = default);

    Task<CampaignEvent> RollDiceAsync(
        string campaignId,
        string actorId,
        string expression,
        CancellationToken cancellationToken = default);
}
