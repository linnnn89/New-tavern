using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Flow.Strategies;

namespace TavernDesk.Core.Flow;

public static class CampaignFlowEngineFactory
{
    public static ICampaignFlowEngine CreateDefault() =>
        new CampaignFlowEngine(new CampaignFlowStrategyRouter(
        [
            new CollaborativeTableStrategy(),
            new BlindSubmissionStrategy(),
            new StrictInitiativeStrategy()
        ]));
}
