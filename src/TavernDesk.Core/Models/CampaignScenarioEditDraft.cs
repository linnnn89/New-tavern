namespace TavernDesk.Core.Models;

public sealed record CampaignScenarioEditDraft(
    string Id,
    CampaignScenario Scenario,
    IReadOnlyList<CampaignScenarioWorldbookBinding> Bindings,
    DateTimeOffset? BaseUpdatedAt,
    string BaselineHash,
    DateTimeOffset SavedAt);
