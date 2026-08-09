using TavernDesk.Core.Models;

namespace TavernDesk.Core.Abstractions;

/// <summary>
/// Shared facade for mode-specific flow strategies.  Callers do not branch on
/// CampaignFlowPreset; the engine routes each operation to one strategy.
/// </summary>
public interface ICampaignFlowEngine
{
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
