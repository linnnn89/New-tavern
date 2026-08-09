using TavernDesk.Core.Models;

namespace TavernDesk.Core.Flow.Strategies;

public sealed class BlindSubmissionStrategy : BatchCampaignFlowStrategy
{
    public override CampaignFlowPreset Preset => CampaignFlowPreset.BlindSubmission;
    protected override CampaignActionExecutionMode ExecutionMode => CampaignActionExecutionMode.Parallel;
    protected override CampaignVisibility PendingVisibility => CampaignVisibility.GmOnly;

    public override bool IsEventVisibleToParticipant(
        CampaignAggregate aggregate, CampaignEvent campaignEvent, CampaignParticipant participant)
    {
        if (campaignEvent.Visibility == CampaignVisibility.Public
            || string.Equals(campaignEvent.ActorId, participant.Id, StringComparison.Ordinal)
            || string.Equals(campaignEvent.RecipientId, participant.Id, StringComparison.Ordinal))
            return true;

        return campaignEvent.Kind == CampaignEventKind.PlayerIntent
               && campaignEvent.RoundNo < aggregate.Campaign.CurrentRound
               && campaignEvent.GenerationStatus == CampaignGenerationStatus.Completed
               && campaignEvent.IsLocked;
    }

    public override bool IsEventVisibleToObserver(
        CampaignAggregate aggregate,
        CampaignEvent campaignEvent) =>
        campaignEvent.Visibility == CampaignVisibility.Public
        || campaignEvent.Kind == CampaignEventKind.PlayerIntent
           && campaignEvent.RoundNo < aggregate.Campaign.CurrentRound
           && campaignEvent.GenerationStatus == CampaignGenerationStatus.Completed
           && campaignEvent.IsLocked;
}
