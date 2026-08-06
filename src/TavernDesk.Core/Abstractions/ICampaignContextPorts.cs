using TavernDesk.Core.Models;

namespace TavernDesk.Core.Abstractions;

public interface ICampaignContextPlanner
{
    Task<CampaignContextPlan> BuildPlayerPlanAsync(
        CampaignAggregate aggregate,
        CampaignParticipant participant,
        CampaignMemoryBank? publicMemory,
        CancellationToken cancellationToken = default,
        bool includeLongTermMemory = true);

    Task<CampaignContextPlan> BuildGmPlanAsync(
        CampaignAggregate aggregate,
        CampaignScenario? scenario,
        CampaignMemoryBank? gmMemory,
        CancellationToken cancellationToken = default,
        bool includeLongTermMemory = true);
}
