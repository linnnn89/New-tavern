using System.Text.Json;
using System.Text.Json.Nodes;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;

namespace TavernDesk.Infrastructure.Compatibility;

public sealed class PresetResolver : IPresetResolver
{
    private static readonly HashSet<string> UnionPaths =
    [
        "prompt.injectionGroups",
        "worldbook.mounts",
        "knowledge.books"
    ];

    private readonly IPresetRepository _repository;

    public PresetResolver(IPresetRepository repository)
    {
        _repository = repository;
    }

    public async Task<ResolvedPreset> ResolveAsync(
        string? characterId,
        string? conversationId,
        CancellationToken cancellationToken = default)
    {
        var diagnostics = new List<string>();
        var layers = new List<JsonObject>();
        await AppendScopeAsync(
            layers,
            diagnostics,
            PresetScopeKind.Global,
            "global",
            cancellationToken);
        if (!string.IsNullOrWhiteSpace(characterId))
        {
            await AppendScopeAsync(
                layers,
                diagnostics,
                PresetScopeKind.Character,
                characterId,
                cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(conversationId))
        {
            await AppendScopeAsync(
                layers,
                diagnostics,
                PresetScopeKind.Conversation,
                conversationId,
                cancellationToken);
        }

        var resolved = new JsonObject();
        foreach (var layer in layers)
        {
            DeepMerge(resolved, layer, string.Empty);
        }

        var systemPrompt = ReadStringAtPath(resolved, "prompt", "systemPrompt")
                           ?? ReadStringAtPath(resolved, "prompt", "system_prompt")
                           ?? ReadStringAtPath(resolved, "system_prompt");
        return new ResolvedPreset(
            resolved.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true
            }),
            systemPrompt,
            diagnostics);
    }

    private async Task AppendScopeAsync(
        ICollection<JsonObject> layers,
        ICollection<string> diagnostics,
        PresetScopeKind scopeKind,
        string scopeId,
        CancellationToken cancellationToken)
    {
        var mounts = await _repository.ListMountsAsync(
            scopeKind,
            scopeId,
            cancellationToken);
        foreach (var mount in mounts
                     .Where(mount => mount.IsEnabled)
                     .OrderBy(mount => mount.SortIndex))
        {
            var preset = await _repository.GetAsync(
                mount.PresetId,
                cancellationToken);
            if (preset is null)
            {
                diagnostics.Add(
                    $"挂载的预设 {mount.PresetId} 不存在，已跳过。");
                continue;
            }

            try
            {
                var overlay = JsonNode.Parse(preset.OverlayJson) as JsonObject
                              ?? throw new JsonException("根节点不是对象。");
                layers.Add(overlay);
                diagnostics.Add(
                    $"已应用 {scopeKind} 预设：{preset.Name}（排序 {mount.SortIndex}）。");
            }
            catch (JsonException exception)
            {
                diagnostics.Add(
                    $"预设“{preset.Name}”JSON 无效，已跳过：{exception.Message}");
            }
        }
    }

    private static void DeepMerge(
        JsonObject target,
        JsonObject source,
        string parentPath)
    {
        foreach (var property in source)
        {
            var path = parentPath.Length == 0
                ? property.Key
                : $"{parentPath}.{property.Key}";
            if (TryReadEntry(property.Value, out var isOn, out var entryValue))
            {
                if (!isOn)
                {
                    continue;
                }

                MergeNode(target, property.Key, entryValue, path);
                continue;
            }

            MergeNode(target, property.Key, property.Value, path);
        }
    }

    private static void MergeNode(
        JsonObject target,
        string key,
        JsonNode? sourceValue,
        string path)
    {
        if (sourceValue is JsonObject sourceObject
            && target[key] is JsonObject targetObject)
        {
            DeepMerge(targetObject, sourceObject, path);
            return;
        }

        if (sourceValue is JsonArray sourceArray
            && UnionPaths.Contains(path))
        {
            var targetArray = target[key] as JsonArray ?? new JsonArray();
            foreach (var item in sourceArray)
            {
                if (!targetArray.Any(existing =>
                        JsonNode.DeepEquals(existing, item)))
                {
                    targetArray.Add(item?.DeepClone());
                }
            }

            target[key] = targetArray;
            return;
        }

        target[key] = sourceValue?.DeepClone();
    }

    private static bool TryReadEntry(
        JsonNode? node,
        out bool isOn,
        out JsonNode? value)
    {
        if (node is JsonObject entry
            && entry.ContainsKey("on")
            && entry.ContainsKey("value")
            && entry["on"] is JsonValue onValue
            && onValue.TryGetValue<bool>(out isOn))
        {
            value = entry["value"];
            return true;
        }

        isOn = true;
        value = node;
        return false;
    }

    private static string? ReadStringAtPath(
        JsonObject source,
        params string[] path)
    {
        JsonNode? current = source;
        foreach (var part in path)
        {
            current = (current as JsonObject)?[part];
        }

        return current is JsonValue value
               && value.TryGetValue<string>(out var result)
            ? result
            : null;
    }
}
