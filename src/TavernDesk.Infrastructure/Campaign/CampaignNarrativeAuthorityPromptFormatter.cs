using System.Text.Encodings.Web;
using System.Text.Json;
using TavernDesk.Core.Models;

namespace TavernDesk.Infrastructure.Campaigns;

internal static class CampaignNarrativeAuthorityPromptFormatter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Format(CampaignNarrativeAuthority authority)
    {
        var authorityData = JsonSerializer.Serialize(
            new
            {
                mode = authority.Preset.ToString(),
                active_intent_ids = authority.ActiveIntentIds,
                active_player_ids = authority.ActiveParticipantIds,
                inactive_player_ids = authority.InactiveParticipantIds,
                permissions = new
                {
                    new_npc = PermissionName(authority.NewNpcPermission),
                    relationship_or_pairing_change =
                        PermissionName(authority.RelationshipChangePermission),
                    independent_plot_thread =
                        PermissionName(authority.IndependentPlotPermission)
                },
                scene_state = authority.State
            },
            JsonOptions);
        var emptyDelta = JsonSerializer.Serialize(
            new CampaignGmNarrativeDelta
            {
                ResolvedPlayerIds = [.. authority.ActiveParticipantIds]
            },
            JsonOptions);
        return
            $"""
            【剧本导演规则与叙事权限｜最高场景约束】
            你是受权裁判，不是自由小说作者。只能创造下列权限明确允许的内容。剧本导演规则高于全局 GM Prompt 中关于“引入新剧情、NPC 或环境变化”的一般性创作许可；但不得覆盖玩家席位所有权、已锁定事实和强制回合协议。

            【当前模式专用裁定边界】
            {authority.ModeContract}

            【冻结剧本导演规则】
            {authority.DirectorInstructions}

            【机器可读权限与场景状态】
            {authorityData}

            权限值：forbidden=禁止；player_intent_only=只有本次已锁定 PlayerIntent 明确授权时才允许，且声明必须填写对应 source_intent_id；gm_discretion=GM 可依据剧本与既有事实自主决定。不得通过机器声明为正文中的越权内容补票。

            在“{CampaignNarrativeProtocol.EvaluationHeader}”之前必须加入独立章节“{CampaignNarrativeProtocol.DeclarationHeader}”，紧接一行 JSON，不使用 Markdown 代码块。准确列出本次裁定的玩家 ID、新增 NPC、关系或互动对象变化、主动开启的独立剧情支线；没有变化时使用空数组。格式示例：
            {CampaignNarrativeProtocol.DeclarationHeader}
            {emptyDelta}
            {CampaignNarrativeProtocol.EvaluationHeader}
            ……
            该声明只供程序校验，保存前会从展示正文中移除。声明缺失、席位不匹配或变化越权时，本候选失败，不推进回合，也不进入记忆。
            """;
    }

    private static string PermissionName(CampaignNarrativePermission permission) =>
        permission switch
        {
            CampaignNarrativePermission.Forbidden => "forbidden",
            CampaignNarrativePermission.PlayerIntentOnly => "player_intent_only",
            CampaignNarrativePermission.GmDiscretion => "gm_discretion",
            _ => throw new ArgumentOutOfRangeException(nameof(permission))
        };
}
