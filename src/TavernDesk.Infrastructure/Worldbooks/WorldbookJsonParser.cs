using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TavernDesk.Core.Models;

namespace TavernDesk.Infrastructure.Worldbooks;

public sealed record ParsedWorldbookDocument(
    bool FoundBook,
    string Name,
    string Description,
    int ScanDepth,
    int TokenBudget,
    bool RecursiveScanning,
    IReadOnlyList<WorldbookEntry> Entries,
    IReadOnlyList<string> Diagnostics);

public static class WorldbookJsonParser
{
    public static ParsedWorldbookDocument Parse(
        string rawJson,
        string fallbackName = "未命名世界书")
    {
        try
        {
            var root = JsonNode.Parse(
                rawJson,
                documentOptions: new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                    MaxDepth = 256
                }) as JsonObject;
            if (root is null)
            {
                return Missing("世界书 JSON 根节点必须是对象。");
            }

            var data = root["data"] as JsonObject ?? root;
            var book = data["character_book"] as JsonObject
                       ?? (root["entries"] is not null ? root : null)
                       ?? (data["entries"] is not null ? data : null);
            if (book is null)
            {
                return Missing("JSON 中没有 character_book 或 entries 世界书节点。");
            }

            var diagnostics = new List<string>();
            var name = ReadString(book, "name")
                       ?? ReadString(book, "title")
                       ?? fallbackName;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = "未命名世界书";
            }

            var entries = ReadEntryNodes(book["entries"]);
            var definitions = entries
                .Select((item, index) => ReadEntry(
                    item.Node,
                    item.ExternalId,
                    index,
                    book,
                    diagnostics))
                .Where(entry => entry is not null)
                .Cast<WorldbookEntry>()
                .ToArray();

            var scanDepth = Math.Clamp(
                ReadInt(book, "scan_depth")
                ?? ReadInt(book, "scanDepth")
                ?? ReadExtensionInt(book, "scan_depth")
                ?? 5,
                0,
                1000);
            var tokenBudget = Math.Clamp(
                ReadInt(book, "token_budget")
                ?? ReadInt(book, "tokenBudget")
                ?? ReadExtensionInt(book, "token_budget")
                ?? 1200,
                0,
                1_000_000);
            var recursive = ReadBool(book, "recursive_scanning")
                            ?? ReadBool(book, "recursiveScanning")
                            ?? ReadExtensionBool(book, "recursive_scanning")
                            ?? true;
            return new ParsedWorldbookDocument(
                true,
                name.Trim(),
                ReadString(book, "description") ?? string.Empty,
                scanDepth,
                tokenBudget,
                recursive,
                definitions,
                diagnostics);
        }
        catch (JsonException exception)
        {
            return Missing($"世界书 JSON 无法解析：{exception.Message}");
        }
        catch (InvalidOperationException exception)
        {
            return Missing($"世界书 JSON 字段格式无效：{exception.Message}");
        }
    }

    private static WorldbookEntry? ReadEntry(
        JsonObject entry,
        string? externalId,
        int index,
        JsonObject book,
        ICollection<string> diagnostics)
    {
        var extensions = entry["extensions"] as JsonObject;
        var id = ReadString(entry, "id")
                 ?? ReadString(entry, "uid")
                 ?? externalId
                 ?? index.ToString(CultureInfo.InvariantCulture);
        var title = ReadString(entry, "name")
                    ?? ReadString(entry, "comment")
                    ?? $"条目 {index + 1}";
        var content = ReadString(entry, "content")?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(content))
        {
            diagnostics.Add($"世界书条目“{title}”没有正文，已跳过。");
            return null;
        }

        var probability = Math.Clamp(
            ReadInt(entry, "probability")
            ?? ReadInt(extensions, "probability")
            ?? 100,
            0,
            100);
        var useProbability = ReadBool(entry, "useProbability")
                             ?? ReadBool(entry, "use_probability")
                             ?? ReadBool(extensions, "useProbability")
                             ?? true;
        var role = (ReadString(entry, "role")
                    ?? ReadString(extensions, "role")
                    ?? "system").Trim().ToLowerInvariant();
        if (role is not ("system" or "user" or "assistant"))
        {
            diagnostics.Add($"世界书条目“{title}”的 role 无效，已按 system 处理。");
            role = "system";
        }

        var extensionsJson = extensions?.ToJsonString() ?? "{}";
        return new WorldbookEntry
        {
            Id = id.Trim(),
            Title = title.Trim(),
            Comment = ReadString(entry, "comment") ?? string.Empty,
            Content = content,
            Keys = ReadStringArray(entry, "keys", "key"),
            SecondaryKeys = ReadStringArray(entry, "secondary_keys", "secondaryKeys"),
            ContentType = ReadContentType(entry, extensions),
            Visibility = ReadVisibility(entry, extensions),
            SemanticEnabled = ReadBool(entry, "semantic_enabled")
                              ?? ReadBool(extensions, "semanticEnabled")
                              ?? true,
            Enabled = ReadBool(entry, "enabled") ?? true,
            Constant = ReadBool(entry, "constant")
                       ?? ReadBool(extensions, "constant")
                       ?? false,
            CaseSensitive = ReadBool(entry, "case_sensitive")
                            ?? ReadBool(extensions, "case_sensitive")
                            ?? ReadBool(book, "case_sensitive")
                            ?? false,
            MatchWholeWords = ReadBool(entry, "match_whole_words")
                              ?? ReadBool(extensions, "match_whole_words")
                              ?? false,
            SelectiveLogic = ReadLogic(entry, extensions),
            InsertionOrder = ReadInt(entry, "insertion_order")
                             ?? ReadInt(entry, "order")
                             ?? 100,
            Position = ReadPosition(entry, extensions, diagnostics, title),
            Depth = Math.Clamp(
                ReadInt(entry, "depth")
                ?? ReadInt(extensions, "depth")
                ?? 4,
                1,
                100),
            ProviderRole = role,
            Probability = probability,
            UseProbability = useProbability,
            InclusionGroup = ReadString(entry, "inclusion_group")
                             ?? ReadString(entry, "group")
                             ?? ReadString(extensions, "inclusion_group")
                             ?? string.Empty,
            GroupWeight = ReadInt(entry, "group_weight")
                          ?? ReadInt(extensions, "group_weight")
                          ?? 100,
            ExcludeRecursion = ReadBool(entry, "exclude_recursion")
                               ?? ReadBool(extensions, "excludeRecursion")
                               ?? false,
            OriginalIndex = index,
            ContentHash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    Encoding.UTF8.GetBytes(content)))
                .ToLowerInvariant(),
            ExtensionsJson = extensionsJson
        };
    }

    private static IReadOnlyList<(JsonObject Node, string? ExternalId)> ReadEntryNodes(
        JsonNode? node)
    {
        if (node is JsonArray array)
        {
            return array
                .OfType<JsonObject>()
                .Select(item => (item, (string?)null))
                .ToArray();
        }

        if (node is JsonObject objectEntries)
        {
            var result = new List<(JsonObject Node, string? ExternalId)>();
            foreach (var item in objectEntries)
            {
                if (item.Value is JsonObject entry)
                {
                    result.Add((entry, item.Key));
                }
            }

            return result;
        }

        return Array.Empty<(JsonObject Node, string? ExternalId)>();
    }

    private static WorldbookContentType ReadContentType(
        JsonObject entry,
        JsonObject? extensions)
    {
        var value = ReadString(entry, "content_type")
                    ?? ReadString(entry, "contentType")
                    ?? ReadString(extensions, "contentType");
        return value?.Trim().ToLowerInvariant() switch
        {
            "instruction" or "instructions" or "command" =>
                WorldbookContentType.Instruction,
            _ => WorldbookContentType.Lore
        };
    }

    private static WorldbookVisibility ReadVisibility(
        JsonObject entry,
        JsonObject? extensions)
    {
        var value = ReadString(entry, "visibility")
                    ?? ReadString(extensions, "visibility");
        return value?.Trim().ToLowerInvariant() switch
        {
            "private" => WorldbookVisibility.Private,
            "gmonly" or "gm_only" => WorldbookVisibility.GmOnly,
            _ => WorldbookVisibility.Public
        };
    }

    private static WorldbookSelectiveLogic ReadLogic(
        JsonObject entry,
        JsonObject? extensions)
    {
        var raw = ReadString(entry, "selective_logic")
                  ?? ReadString(entry, "logic")
                  ?? ReadString(extensions, "selective_logic");
        if (raw is not null)
        {
            return raw.Replace("_", string.Empty).ToLowerInvariant() switch
            {
                "andall" => WorldbookSelectiveLogic.AndAll,
                "notany" => WorldbookSelectiveLogic.NotAny,
                "notall" => WorldbookSelectiveLogic.NotAll,
                _ => WorldbookSelectiveLogic.AndAny
            };
        }

        var numeric = ReadInt(entry, "selectiveLogic")
                      ?? ReadInt(entry, "selective_logic")
                      ?? ReadInt(extensions, "selectiveLogic")
                      ?? ReadInt(extensions, "selective_logic");
        return numeric switch
        {
            1 => WorldbookSelectiveLogic.AndAll,
            2 => WorldbookSelectiveLogic.NotAny,
            3 => WorldbookSelectiveLogic.NotAll,
            _ => WorldbookSelectiveLogic.AndAny
        };
    }

    private static WorldbookInsertionPosition ReadPosition(
        JsonObject entry,
        JsonObject? extensions,
        ICollection<string> diagnostics,
        string title)
    {
        var raw = ReadString(entry, "position")
                  ?? ReadString(extensions, "position");
        if (!string.IsNullOrWhiteSpace(raw))
        {
            return raw.Trim().ToLowerInvariant() switch
            {
                "before_char" or "beforecharacter" or "before_character" =>
                    WorldbookInsertionPosition.BeforeCharacter,
                "after_char" or "aftercharacter" or "after_character" =>
                    WorldbookInsertionPosition.AfterCharacter,
                "at_depth" or "atdepth" or "chat_history" =>
                    WorldbookInsertionPosition.HistoryDepth,
                _ => WorldbookInsertionPosition.AfterCharacter
            };
        }

        var numeric = ReadInt(entry, "position")
                      ?? ReadInt(extensions, "position");
        return numeric switch
        {
            null or 1 => WorldbookInsertionPosition.AfterCharacter,
            0 => WorldbookInsertionPosition.BeforeCharacter,
            4 or 6 => WorldbookInsertionPosition.HistoryDepth,
            _ => UnsupportedPosition(diagnostics, title, numeric.Value)
        };
    }

    private static WorldbookInsertionPosition UnsupportedPosition(
        ICollection<string> diagnostics,
        string title,
        int position)
    {
        diagnostics.Add(
            $"世界书条目“{title}”的位置 {position} 暂无独立槽位，已按角色设定后处理。");
        return WorldbookInsertionPosition.AfterCharacter;
    }

    private static IReadOnlyList<string> ReadStringArray(
        JsonObject source,
        params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (source[propertyName] is JsonArray array)
            {
                return array
                    .OfType<JsonValue>()
                    .Select(value => value.TryGetValue<string>(out var result)
                        ? result.Trim()
                        : string.Empty)
                    .Where(value => value.Length > 0)
                    .ToArray();
            }

            if (ReadString(source, propertyName) is { Length: > 0 } value)
            {
                return [value.Trim()];
            }
        }

        return Array.Empty<string>();
    }

    private static ParsedWorldbookDocument Missing(string diagnostic) =>
        new(false, string.Empty, string.Empty, 5, 1200, true, [], [diagnostic]);

    private static string? ReadString(JsonObject? source, string propertyName) =>
        source?[propertyName] is JsonValue value
        && value.TryGetValue<string>(out var result)
            ? result
            : null;

    private static bool? ReadBool(JsonObject? source, string propertyName)
    {
        if (source?[propertyName] is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<bool>(out var boolean))
        {
            return boolean;
        }

        if (value.TryGetValue<int>(out var integer))
        {
            return integer != 0;
        }

        return value.TryGetValue<string>(out var text)
            && bool.TryParse(text, out var parsed)
            ? parsed
            : null;
    }

    private static bool? ReadExtensionBool(JsonObject source, string propertyName) =>
        ReadBool(source["extensions"] as JsonObject, propertyName);

    private static int? ReadInt(JsonObject? source, string propertyName)
    {
        if (source?[propertyName] is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<int>(out var integer))
        {
            return integer;
        }

        if (value.TryGetValue<long>(out var longValue)
            && longValue is >= int.MinValue and <= int.MaxValue)
        {
            return (int)longValue;
        }

        return value.TryGetValue<string>(out var text)
               && int.TryParse(
                   text,
                   NumberStyles.Integer,
                   CultureInfo.InvariantCulture,
                   out var parsed)
            ? parsed
            : null;
    }

    private static int? ReadExtensionInt(JsonObject source, string propertyName) =>
        ReadInt(source["extensions"] as JsonObject, propertyName);
}
