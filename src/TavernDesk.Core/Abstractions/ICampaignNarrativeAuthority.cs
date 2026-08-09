using TavernDesk.Core.Models;

namespace TavernDesk.Core.Abstractions;

public interface ICampaignNarrativeAuthorityCompiler
{
    CampaignNarrativeAuthority Compile(
        CampaignAggregate aggregate,
        CampaignResolutionPlan resolutionPlan,
        string directorInstructions);
}

public interface ICampaignGmOutputValidator
{
    CampaignGmValidationResult Validate(
        string content,
        CampaignNarrativeAuthority authority);
}
