using TavernDesk.Core.Models;

namespace TavernDesk.Core.Narrative.Strategies;

public sealed class CollaborativeNarrativeAuthorityStrategy
    : ICampaignNarrativeAuthorityStrategy
{
    public CampaignFlowPreset Preset => CampaignFlowPreset.CollaborativeTable;

    public string BuildModeContract(
        CampaignAggregate aggregate,
        IReadOnlyList<CampaignEvent> activeIntents) =>
        "协作圆桌：本次只裁定列出的全部已锁定 PlayerIntent。可以让这些已提交行动彼此产生因果，但玩家对其他席位的建议不等于对方已经行动；不得为任何席位补写未提交的台词、心理、决定或下一步行动。";
}
