using TavernDesk.Core.Models;

namespace TavernDesk.Core.Abstractions;

/// <summary>
/// Mode-specific campaign rules.  Implementations are pure projections and
/// validation plans; persistence and provider execution remain in CampaignRunner.
/// </summary>
public interface ICampaignFlowStrategy
{
    CampaignFlowPreset Preset { get; }

    CampaignFlowSnapshot Inspect(CampaignAggregate aggregate);

    CampaignActionPlan PlanAction(
        CampaignAggregate aggregate,
        string participantId);

    CampaignResolutionPlan PlanResolution(CampaignAggregate aggregate);

    CampaignAdvancePlan PlanAdvance(
        CampaignAggregate aggregate,
        CampaignEvent resolution,
        bool commitCandidate);

    bool IsEventVisibleToParticipant(
        CampaignAggregate aggregate,
        CampaignEvent campaignEvent,
        CampaignParticipant participant);

    bool IsEventVisibleToObserver(
        CampaignAggregate aggregate,
        CampaignEvent campaignEvent);
}
