using TavernDesk.Core.Models;

namespace TavernDesk.Core.Narrative.Strategies;

public sealed class BlindSubmissionNarrativeAuthorityStrategy
    : ICampaignNarrativeAuthorityStrategy
{
    public CampaignFlowPreset Preset => CampaignFlowPreset.BlindSubmission;

    public string BuildModeContract(
        CampaignAggregate aggregate,
        IReadOnlyList<CampaignEvent> activeIntents) =>
        "秘密同投：本次同时裁定全部已锁定 PlayerIntent。各行动在提交时互不可见，GM 可以处理它们的客观碰撞与后果，但不得假定玩家预先知道其他秘密行动，也不得替任何席位追加行动、台词或选择。";
}
