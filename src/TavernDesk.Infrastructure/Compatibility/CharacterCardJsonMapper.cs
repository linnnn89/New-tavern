using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using TavernDesk.Core.Models;

namespace TavernDesk.Infrastructure.Compatibility;

internal sealed record CharacterCardJsonParseResult(
    Character Character,
    string Spec,
    string SpecVersion,
    IReadOnlyList<string> UnknownFieldPaths,
    IReadOnlyList<string> Warnings,
    JsonObject Root,
    JsonObject Data);

internal static class CharacterCardJsonMapper
{
    private static readonly HashSet<string> RootFields =
    [
        "spec",
        "spec_version",
        "data"
    ];

    private static readonly HashSet<string> DataFields =
    [
        "name",
        "description",
        "personality",
        "scenario",
        "first_mes",
        "mes_example",
        "creator_notes",
        "system_prompt",
        "post_history_instructions",
        "alternate_greetings",
        "character_book",
        "tags",
        "creator",
        "character_version",
        "extensions",
        "assets",
        "nickname",
        "creator_notes_multilingual",
        "source",
        "group_only_greetings",
        "creation_date",
        "modification_date"
    ];

    public static CharacterCardJsonParseResult Parse(
        string json,
        string fallbackName)
    {
        var root = JsonNode.Parse(
            json,
            documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 256
            }) as JsonObject
            ?? throw new InvalidDataException("角色卡 JSON 根节点必须是对象。");

        var warnings = new List<string>();
        var spec = ReadString(root, "spec", warnings, "$") ?? string.Empty;
        var specVersion = ReadString(root, "spec_version", warnings, "$") ?? string.Empty;
        var hasWrappedData = root["data"] is JsonObject;
        if (spec is "chara_card_v2" or "chara_card_v3" && !hasWrappedData)
        {
            throw new InvalidDataException($"{spec} 角色卡缺少 data 对象。");
        }

        var data = hasWrappedData
            ? (JsonObject)root["data"]!
            : root;
        var dataPath = hasWrappedData ? "$.data" : "$";
        var name = ReadString(data, "name", warnings, hasWrappedData ? "$.data" : "$")
                   ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = fallbackName;
            warnings.Add("角色卡缺少有效 name，已使用文件名。");
        }

        if (spec == "chara_card_v3"
            && double.TryParse(
                specVersion,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsedVersion)
            && parsedVersion > 3d)
        {
            warnings.Add($"角色卡版本 {specVersion} 高于当前完整支持的 3.0；未知字段将保留。");
        }

        if (string.IsNullOrWhiteSpace(spec))
        {
            warnings.Add(hasWrappedData
                ? "角色卡带 data 包装但没有 spec；已按兼容格式导入。"
                : "已按 legacy/V1 角色卡导入。");
        }

        var unknownPaths = new List<string>();
        if (hasWrappedData)
        {
            unknownPaths.AddRange(root
                .Where(property => !RootFields.Contains(property.Key))
                .Select(property => $"$.{property.Key}"));
        }

        unknownPaths.AddRange(data
            .Where(property => !DataFields.Contains(property.Key))
            .Select(property => hasWrappedData
                ? $"$.data.{property.Key}"
                : $"$.{property.Key}"));

        var character = new Character
        {
            Name = name.Trim(),
            Description = ReadString(data, "description", warnings, dataPath) ?? string.Empty,
            Personality = ReadString(data, "personality", warnings, dataPath) ?? string.Empty,
            Scenario = ReadString(data, "scenario", warnings, dataPath) ?? string.Empty,
            FirstMessage = ReadString(data, "first_mes", warnings, dataPath) ?? string.Empty,
            RawCardJson = root.ToJsonString(),
            UpdatedAt = DateTimeOffset.Now
        };

        return new CharacterCardJsonParseResult(
            character,
            string.IsNullOrWhiteSpace(spec)
                ? (hasWrappedData ? "wrapped_legacy" : "legacy_v1")
                : spec,
            specVersion,
            unknownPaths.Distinct(StringComparer.Ordinal).Order().ToArray(),
            warnings,
            root,
            data);
    }

    public static JsonObject UpdatePreservingShape(Character character)
    {
        var root = ParseObjectOrEmpty(character.RawCardJson);
        if (root.Count == 0 && string.IsNullOrWhiteSpace(character.SourceCardPath))
        {
            return ToV3(character);
        }

        var data = root["data"] as JsonObject ?? root;
        SetBaseFields(data, character);
        return root;
    }

    public static JsonObject ToV3(
        Character character,
        JsonArray? assetsOverride = null)
    {
        var sourceRoot = ParseObjectOrEmpty(character.RawCardJson);
        var sourceData = sourceRoot["data"] as JsonObject;
        var targetRoot = new JsonObject();

        if (sourceData is not null)
        {
            foreach (var property in sourceRoot)
            {
                if (property.Key is not ("spec" or "spec_version" or "data"))
                {
                    targetRoot[property.Key] = property.Value?.DeepClone();
                }
            }
        }

        var targetData = (sourceData ?? sourceRoot).DeepClone() as JsonObject
                         ?? new JsonObject();
        targetRoot["spec"] = "chara_card_v3";
        targetRoot["spec_version"] = "3.0";
        targetRoot["data"] = targetData;
        SetBaseFields(targetData, character);
        EnsureString(targetData, "mes_example");
        EnsureString(targetData, "creator_notes");
        EnsureString(targetData, "system_prompt");
        EnsureString(targetData, "post_history_instructions");
        EnsureString(targetData, "creator");
        EnsureString(targetData, "character_version");
        EnsureArray(targetData, "alternate_greetings");
        EnsureArray(targetData, "tags");
        EnsureArray(targetData, "group_only_greetings");
        EnsureObject(targetData, "extensions");
        if (assetsOverride is not null)
        {
            targetData["assets"] = assetsOverride.DeepClone();
        }

        targetData["modification_date"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return targetRoot;
    }

    public static JsonObject ToV2Backfill(Character character)
    {
        var sourceRoot = ParseObjectOrEmpty(character.RawCardJson);
        var sourceData = sourceRoot["data"] as JsonObject ?? sourceRoot;
        var data = new JsonObject();
        foreach (var field in new[]
                 {
                     "mes_example",
                     "creator_notes",
                     "system_prompt",
                     "post_history_instructions",
                     "alternate_greetings",
                     "character_book",
                     "tags",
                     "creator",
                     "character_version",
                     "extensions"
                 })
        {
            if (sourceData[field] is { } value)
            {
                data[field] = value.DeepClone();
            }
        }

        SetBaseFields(data, character);
        EnsureString(data, "mes_example");
        EnsureString(data, "creator_notes");
        EnsureString(data, "system_prompt");
        EnsureString(data, "post_history_instructions");
        EnsureString(data, "creator");
        EnsureString(data, "character_version");
        EnsureArray(data, "alternate_greetings");
        EnsureArray(data, "tags");
        EnsureObject(data, "extensions");

        var note = data["creator_notes"]?.GetValue<string>() ?? string.Empty;
        const string compatibilityWarning =
            "This character card also contains Character Card V3 data. "
            + "Use a Character Card V3 compatible application for the complete card.";
        if (!note.Contains(compatibilityWarning, StringComparison.Ordinal))
        {
            data["creator_notes"] = string.IsNullOrWhiteSpace(note)
                ? compatibilityWarning
                : $"{note.TrimEnd()}\n\n{compatibilityWarning}";
        }

        return new JsonObject
        {
            ["spec"] = "chara_card_v2",
            ["spec_version"] = "2.0",
            ["data"] = data
        };
    }

    public static string Serialize(JsonNode node, bool indented = false) =>
        node.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = indented
        });

    private static JsonObject ParseObjectOrEmpty(string rawJson)
    {
        try
        {
            return JsonNode.Parse(rawJson) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private static string? ReadString(
        JsonObject source,
        string propertyName,
        ICollection<string> warnings,
        string path)
    {
        if (source[propertyName] is null)
        {
            return null;
        }

        if (source[propertyName] is JsonValue value
            && value.TryGetValue<string>(out var result))
        {
            return result;
        }

        warnings.Add($"{path}.{propertyName} 不是字符串，未映射到可编辑基础字段；原值仍会保留。");
        return null;
    }

    private static void SetBaseFields(JsonObject data, Character character)
    {
        data["name"] = character.Name;
        data["description"] = character.Description;
        data["personality"] = character.Personality;
        data["scenario"] = character.Scenario;
        data["first_mes"] = character.FirstMessage;
    }

    private static void EnsureString(JsonObject data, string propertyName)
    {
        if (data[propertyName] is not JsonValue value
            || !value.TryGetValue<string>(out _))
        {
            data[propertyName] = string.Empty;
        }
    }

    private static void EnsureArray(JsonObject data, string propertyName)
    {
        if (data[propertyName] is not JsonArray)
        {
            data[propertyName] = new JsonArray();
        }
    }

    private static void EnsureObject(JsonObject data, string propertyName)
    {
        if (data[propertyName] is not JsonObject)
        {
            data[propertyName] = new JsonObject();
        }
    }
}
