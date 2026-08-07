using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;

namespace TavernDesk.Infrastructure.Compatibility;

public sealed class CampaignCharacterSnapshotAdapter
    : ICampaignCharacterSnapshotAdapter
{
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public CampaignCharacterSnapshotResult Create(
        Character character,
        string? memoryBody,
        bool includeMemory,
        bool includeOriginalWorldKnowledge)
    {
        ArgumentNullException.ThrowIfNull(character);
        var root = JsonNode.Parse(character.RawCardJson) as JsonObject
                   ?? new JsonObject();
        var data = root["data"] as JsonObject ?? root;
        var sourceHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(character.RawCardJson)))
            .ToLowerInvariant();
        var snapshot = new JsonObject
        {
            ["schema"] = "taverndesk.campaign-character.v1",
            ["source"] = new JsonObject
            {
                ["character_id"] = character.Id,
                ["spec"] = ReadString(root, "spec"),
                ["spec_version"] = ReadString(root, "spec_version"),
                ["sha256"] = sourceHash
            },
            ["identity"] = new JsonObject
            {
                ["name"] = character.Name,
                ["description"] = character.Description,
                ["personality"] = character.Personality
            },
            ["behavior"] = new JsonObject
            {
                ["dialogue_examples"] = ReadString(data, "mes_example")
            },
            ["excluded_from_campaign_prompt"] = new JsonArray
            {
                "first_mes",
                "alternate_greetings",
                "scenario",
                "system_prompt",
                "post_history_instructions",
                "character_book",
                "creator_notes",
                "source_import_report"
            }
        };
        var originalWorld = new JsonObject();
        if (includeOriginalWorldKnowledge)
        {
            originalWorld["scenario"] = character.Scenario;
            if (data["character_book"] is { } book)
            {
                originalWorld["character_book"] = book.DeepClone();
            }
        }

        var warnings = new List<string>
        {
            "跑团角色卡只发送 name、description、personality 和 mes_example。",
            "first_mes、alternate_greetings、scenario、系统提示词、后置提示词和角色世界书已排除。",
            "源角色卡 RawCardJson 保持原样，跑团只读取独立快照。"
        };
        if (!includeMemory && !string.IsNullOrWhiteSpace(memoryBody))
        {
            warnings.Add("普通聊天记忆未导入跑团。");
        }

        if (!includeOriginalWorldKnowledge
            && (!string.IsNullOrWhiteSpace(character.Scenario)
                || data["character_book"] is not null))
        {
            warnings.Add("原世界场景与角色世界书未导入跑团。");
        }

        return new CampaignCharacterSnapshotResult(
            snapshot.ToJsonString(SnapshotJsonOptions),
            includeMemory ? memoryBody?.Trim() ?? string.Empty : string.Empty,
            originalWorld.ToJsonString(SnapshotJsonOptions),
            warnings);
    }

    private static string ReadString(JsonObject source, string propertyName) =>
        source[propertyName] is JsonValue value
        && value.TryGetValue<string>(out var text)
            ? text
            : string.Empty;
}
