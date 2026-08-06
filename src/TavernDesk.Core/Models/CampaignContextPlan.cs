using TavernDesk.Core.Abstractions;

namespace TavernDesk.Core.Models;

public enum CampaignContextPlanStatus
{
    Ready,
    HistoryTrimmed,
    BlockedMandatoryContextTooLarge
}

public sealed record CampaignContextSectionEstimate(
    string Id,
    string Title,
    ContextSegmentKind Kind,
    int EstimatedTokens,
    bool IsMandatory,
    bool WasIncluded,
    bool WasTruncated);

public sealed record CampaignContextPlan(
    IReadOnlyList<ProviderChatMessage> Messages,
    IReadOnlyList<CampaignContextSectionEstimate> Sections,
    TokenEstimate Estimate,
    CampaignContextPlanStatus Status,
    string? BlockingReason = null)
{
    public bool CanGenerate =>
        Status != CampaignContextPlanStatus.BlockedMandatoryContextTooLarge
        && !Estimate.ExceedsLimit;
}
