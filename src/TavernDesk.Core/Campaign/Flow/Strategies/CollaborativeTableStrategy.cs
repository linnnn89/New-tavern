using TavernDesk.Core.Models;

namespace TavernDesk.Core.Flow.Strategies;

public sealed class CollaborativeTableStrategy : BatchCampaignFlowStrategy
{
    public override CampaignFlowPreset Preset => CampaignFlowPreset.CollaborativeTable;
    protected override CampaignActionExecutionMode ExecutionMode => CampaignActionExecutionMode.Sequential;
    protected override CampaignVisibility PendingVisibility => CampaignVisibility.Public;

    public override bool IsEventVisibleToParticipant(
        CampaignAggregate aggregate, CampaignEvent campaignEvent, CampaignParticipant participant) =>
        campaignEvent.Visibility == CampaignVisibility.Public
        || string.Equals(campaignEvent.ActorId, participant.Id, StringComparison.Ordinal)
        || string.Equals(campaignEvent.RecipientId, participant.Id, StringComparison.Ordinal);

    public override bool IsEventVisibleToObserver(
        CampaignAggregate aggregate,
        CampaignEvent campaignEvent) =>
        campaignEvent.Visibility == CampaignVisibility.Public;
}
