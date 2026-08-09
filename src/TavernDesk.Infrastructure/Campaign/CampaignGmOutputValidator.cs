using System.Text.Json;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;

namespace TavernDesk.Infrastructure.Campaigns;

public sealed class CampaignGmOutputValidator : ICampaignGmOutputValidator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public CampaignGmValidationResult Validate(
        string? content,
        CampaignNarrativeAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        var normalized = (content ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
        var declarationIndex = normalized.LastIndexOf(
            CampaignNarrativeProtocol.DeclarationHeader,
            StringComparison.Ordinal);
        var evaluationIndex = normalized.LastIndexOf(
            CampaignNarrativeProtocol.EvaluationHeader,
            StringComparison.Ordinal);
        if (declarationIndex < 0
            || evaluationIndex < 0
            || declarationIndex >= evaluationIndex)
        {
            return Invalid(
                content,
                "GM 缺少位于最终评定章节之前的叙事权限声明。");
        }

        var jsonStart = declarationIndex
                        + CampaignNarrativeProtocol.DeclarationHeader.Length;
        var json = normalized[jsonStart..evaluationIndex].Trim();
        var before = normalized[..declarationIndex].TrimEnd();
        var evaluation = normalized[evaluationIndex..].TrimStart();
        var display = $"{before}\n\n{evaluation}".Trim();
        CampaignGmNarrativeDelta? delta;
        try
        {
            delta = ParseDelta(json);
        }
        catch (JsonException)
        {
            return Invalid(display, "GM 叙事权限声明不是有效 JSON。");
        }

        if (delta is null || delta.SchemaVersion != 1)
        {
            return Invalid(display, "GM 叙事权限声明版本无效。");
        }

        delta.ResolvedPlayerIds ??= [];
        delta.IntroducedNpcs ??= [];
        delta.RelationshipChanges ??= [];
        delta.StartedPlotThreads ??= [];
        var declaredPlayers = delta.ResolvedPlayerIds
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .ToHashSet(StringComparer.Ordinal);
        var expectedPlayers = authority.ActiveParticipantIds
            .ToHashSet(StringComparer.Ordinal);
        if (!declaredPlayers.SetEquals(expectedPlayers))
        {
            return Invalid(
                display,
                "GM 声明裁定的玩家席位与当前流程授权席位不一致。");
        }

        var activeIntentIds = authority.ActiveIntentIds
            .ToHashSet(StringComparer.Ordinal);
        var failure = ValidateChanges(
                          "新增 NPC",
                          delta.IntroducedNpcs,
                          authority.NewNpcPermission,
                          activeIntentIds)
                      ?? ValidateChanges(
                          "关系或互动对象变化",
                          delta.RelationshipChanges,
                          authority.RelationshipChangePermission,
                          activeIntentIds)
                      ?? ValidateChanges(
                          "独立剧情支线",
                          delta.StartedPlotThreads,
                          authority.IndependentPlotPermission,
                          activeIntentIds);
        if (failure is not null)
        {
            return Invalid(display, failure);
        }
        return new CampaignGmValidationResult(
            true,
            display,
            JsonSerializer.Serialize(delta, JsonOptions),
            null,
            delta);
    }

    private static CampaignGmNarrativeDelta ParseDelta(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("叙事权限声明必须是 JSON 对象。");
        }

        return new CampaignGmNarrativeDelta
        {
            SchemaVersion = ReadInt32(root, "schema_version", 0),
            ResolvedPlayerIds = ReadStringArray(root, "resolved_player_ids"),
            IntroducedNpcs = ReadChanges(root, "introduced_npcs"),
            RelationshipChanges = ReadChanges(root, "relationship_changes"),
            StartedPlotThreads = ReadChanges(root, "started_plot_threads")
        };
    }

    private static int ReadInt32(
        JsonElement root,
        string propertyName,
        int fallback)
    {
        if (!TryGetProperty(root, propertyName, out var property))
        {
            return fallback;
        }

        if (property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt32(out var value))
        {
            throw new JsonException($"{propertyName} 必须是整数。");
        }

        return value;
    }

    private static List<string> ReadStringArray(
        JsonElement root,
        string propertyName)
    {
        if (!TryGetProperty(root, propertyName, out var property))
        {
            return [];
        }

        if (property.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException($"{propertyName} 必须是数组。");
        }

        var result = new List<string>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new JsonException($"{propertyName} 只能包含字符串。");
            }

            result.Add(item.GetString() ?? string.Empty);
        }

        return result;
    }

    private static List<CampaignNarrativeChange> ReadChanges(
        JsonElement root,
        string propertyName)
    {
        if (!TryGetProperty(root, propertyName, out var property))
        {
            return [];
        }

        if (property.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException($"{propertyName} 必须是数组。");
        }

        var result = new List<CampaignNarrativeChange>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                result.Add(new CampaignNarrativeChange
                {
                    Description = item.GetString() ?? string.Empty
                });
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object
                || !TryGetProperty(item, "description", out var description)
                || description.ValueKind != JsonValueKind.String)
            {
                throw new JsonException(
                    $"{propertyName} 只能包含字符串或变化对象。");
            }

            string? sourceIntentId = null;
            if (TryGetProperty(item, "source_intent_id", out var source))
            {
                sourceIntentId = source.ValueKind switch
                {
                    JsonValueKind.Null => null,
                    JsonValueKind.String => source.GetString(),
                    _ => throw new JsonException(
                        "source_intent_id 必须是字符串或 null。")
                };
            }

            result.Add(new CampaignNarrativeChange
            {
                Description = description.GetString() ?? string.Empty,
                SourceIntentId = sourceIntentId
            });
        }

        return result;
    }

    private static bool TryGetProperty(
        JsonElement element,
        string propertyName,
        out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(
                    property.Name,
                    propertyName,
                    StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? ValidateChanges(
        string label,
        IReadOnlyList<CampaignNarrativeChange> changes,
        CampaignNarrativePermission permission,
        IReadOnlySet<string> activeIntentIds)
    {
        if (changes.Any(item => string.IsNullOrWhiteSpace(item.Description)))
        {
            return $"{label}声明包含空白项目。";
        }

        if (permission == CampaignNarrativePermission.Forbidden
            && changes.Count > 0)
        {
            return $"当前剧本禁止 GM 创建{label}。";
        }

        foreach (var change in changes)
        {
            var source = change.SourceIntentId?.Trim();
            if (permission == CampaignNarrativePermission.PlayerIntentOnly
                && (string.IsNullOrWhiteSpace(source)
                    || !activeIntentIds.Contains(source)))
            {
                return $"{label}必须关联本次已锁定的 PlayerIntent。";
            }

            if (!string.IsNullOrWhiteSpace(source)
                && !activeIntentIds.Contains(source))
            {
                return $"{label}引用了不属于本次裁定的 PlayerIntent。";
            }
        }

        return null;
    }

    private static CampaignGmValidationResult Invalid(
        string? content,
        string reason) =>
        new(false, content ?? string.Empty, "{}", reason, null);
}
