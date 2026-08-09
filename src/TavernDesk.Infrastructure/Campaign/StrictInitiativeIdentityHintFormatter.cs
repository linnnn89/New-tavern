using TavernDesk.Core.Models;

namespace TavernDesk.Infrastructure.Campaigns;

internal static class StrictInitiativeIdentityHintFormatter
{
    public static string Format(
        CampaignAggregate aggregate,
        CampaignResolutionPlan resolutionPlan)
    {
        if (aggregate.Campaign.FlowPreset != CampaignFlowPreset.StrictInitiative)
        {
            return string.Empty;
        }

        var activeIntentIds = resolutionPlan.PlayerIntentIds
            .ToHashSet(StringComparer.Ordinal);
        var currentActorIds = aggregate.Events
            .Where(item => activeIntentIds.Contains(item.Id))
            .Where(item => item.Kind == CampaignEventKind.PlayerIntent)
            .Select(item => item.ActorId)
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToArray();
        if (currentActorIds.Length != 1)
        {
            return string.Empty;
        }

        var currentParticipant = aggregate.Participants.FirstOrDefault(item =>
            item.Id == currentActorIds[0]
            && item.IsEnabled);
        if (currentParticipant?.Kind != CampaignParticipantKind.User)
        {
            return string.Empty;
        }

        var playerNames = aggregate.Participants
            .Where(item => item.IsEnabled)
            .OrderBy(item => item.SortIndex)
            .Select(item => item.DisplayName.Trim())
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (playerNames.Length == 0)
        {
            return string.Empty;
        }

        return $"【严格先攻身份提示】本局玩家仅有：{string.Join("、", playerNames)}；本条 USER 发言中出现的其他姓名默认视为 NPC，不是玩家席位。";
    }
}
