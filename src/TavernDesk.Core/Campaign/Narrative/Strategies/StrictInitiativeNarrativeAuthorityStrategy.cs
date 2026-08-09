using TavernDesk.Core.Models;

namespace TavernDesk.Core.Narrative.Strategies;

public sealed class StrictInitiativeNarrativeAuthorityStrategy
    : ICampaignNarrativeAuthorityStrategy
{
    public CampaignFlowPreset Preset => CampaignFlowPreset.StrictInitiative;

    public string BuildModeContract(
        CampaignAggregate aggregate,
        IReadOnlyList<CampaignEvent> activeIntents)
    {
        if (activeIntents.Count != 1)
        {
            throw new InvalidOperationException(
                "严格先攻的一次 GM 裁定必须且只能包含一个玩家行动。");
        }

        var actorId = activeIntents[0].ActorId;
        var actor = aggregate.Participants.FirstOrDefault(item =>
            string.Equals(item.Id, actorId, StringComparison.Ordinal));
        var name = actor?.DisplayName ?? actorId;
        return
            $"严格先攻：当前唯一有行动权的席位是“{name}”（ID={actorId}）。只裁定该席位已经提交的行动。其他玩家席位必须保持等待状态；即使当前玩家在正文中要求、建议或描述其他玩家如何行动，也不得替那些席位生成台词、心理、选择、反应或行动。不在本局玩家席位名单中的姓名默认属于 NPC；世界、环境和 NPC 可以响应当前行动，但不得借此重排其他玩家的行动。";
    }
}
