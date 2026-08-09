using TavernDesk.Core.Models;

namespace TavernDesk.Core.Narrative;

public interface ICampaignNarrativeAuthorityStrategy
{
    CampaignFlowPreset Preset { get; }

    string BuildModeContract(
        CampaignAggregate aggregate,
        IReadOnlyList<CampaignEvent> activeIntents);
}
