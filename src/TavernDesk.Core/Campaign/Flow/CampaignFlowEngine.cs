using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;

namespace TavernDesk.Core.Flow;

/// <summary>
/// Thin routing facade.  It owns no mode-specific rules and no persistence;
/// every decision is delegated to the strategy selected by the campaign preset.
/// </summary>
public sealed class CampaignFlowEngine : ICampaignFlowEngine
{
    private readonly CampaignFlowStrategyRouter _router;

    public CampaignFlowEngine(CampaignFlowStrategyRouter router)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
    }

    public CampaignFlowSnapshot Inspect(CampaignAggregate aggregate)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        return _router.Resolve(aggregate.Campaign.FlowPreset)
            .Inspect(aggregate);
    }

    public CampaignActionPlan PlanAction(
        CampaignAggregate aggregate,
        string participantId)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        ArgumentException.ThrowIfNullOrWhiteSpace(participantId);
        return _router.Resolve(aggregate.Campaign.FlowPreset)
            .PlanAction(aggregate, participantId);
    }

    public CampaignResolutionPlan PlanResolution(
        CampaignAggregate aggregate)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        return _router.Resolve(aggregate.Campaign.FlowPreset)
            .PlanResolution(aggregate);
    }

    public CampaignAdvancePlan PlanAdvance(
        CampaignAggregate aggregate,
        CampaignEvent resolution,
        bool commitCandidate)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        ArgumentNullException.ThrowIfNull(resolution);
        return _router.Resolve(aggregate.Campaign.FlowPreset)
            .PlanAdvance(aggregate, resolution, commitCandidate);
    }

    public bool IsEventVisibleToParticipant(
        CampaignAggregate aggregate,
        CampaignEvent campaignEvent,
        CampaignParticipant participant)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        ArgumentNullException.ThrowIfNull(campaignEvent);
        ArgumentNullException.ThrowIfNull(participant);
        return _router.Resolve(aggregate.Campaign.FlowPreset)
            .IsEventVisibleToParticipant(aggregate, campaignEvent, participant);
    }

    public bool IsEventVisibleToObserver(
        CampaignAggregate aggregate,
        CampaignEvent campaignEvent)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        ArgumentNullException.ThrowIfNull(campaignEvent);
        return _router.Resolve(aggregate.Campaign.FlowPreset)
            .IsEventVisibleToObserver(aggregate, campaignEvent);
    }
}
