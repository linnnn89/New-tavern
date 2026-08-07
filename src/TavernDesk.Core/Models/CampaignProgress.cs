namespace TavernDesk.Core.Models;

public sealed record CampaignGenerationProgress(
    string CampaignId,
    string EventId,
    CampaignEventKind EventKind,
    string ActorId,
    CampaignGenerationStatus Status,
    int ReceivedTokens,
    int? CompletionTokens = null,
    string? Message = null);

public enum CampaignMemoryUpdateProgressStatus
{
    Started,
    Receiving,
    Completed,
    Failed
}

public sealed record CampaignMemoryUpdateProgress(
    string CampaignId,
    CampaignMemoryScope? Scope,
    CampaignMemoryUpdateProgressStatus Status,
    int ReceivedTokens,
    bool IsAutomatic,
    string? Message = null,
    string? OperationId = null);
