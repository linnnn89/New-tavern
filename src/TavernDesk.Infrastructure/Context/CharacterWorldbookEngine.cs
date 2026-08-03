using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;

namespace TavernDesk.Infrastructure.Context;

public sealed class CharacterWorldbookEngine : IWorldbookEngine
{
    private readonly IMacroEngine _macros;

    public CharacterWorldbookEngine(IMacroEngine macros)
    {
        _macros = macros;
    }

    public Task<WorldbookScanResult> ScanAsync(
        WorldbookScanRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var data = ReadCardData(request.RawCardJson);
        if (data?["character_book"] is not JsonObject book
            || book["entries"] is not JsonArray entries)
        {
            return Task.FromResult(new WorldbookScanResult([], []));
        }

        var diagnostics = new List<string>();
        var definitions = entries
            .OfType<JsonObject>()
            .Select((entry, index) => ReadEntry(
                entry,
                index,
                book,
                diagnostics))
            .Where(entry => entry is not null)
            .Cast<EntryDefinition>()
            .ToArray();
        var scanDepth = Math.Clamp(
            ReadInt32(book, "scan_depth")
            ?? ReadExtensionInt32(book, "scan_depth")
            ?? request.DefaultScanDepth,
            0,
            1000);
        var scanMessages = scanDepth == 0
            ? Array.Empty<ChatMessage>()
            : request.Messages.TakeLast(scanDepth).ToArray();
        var scan = new StringBuilder();
        foreach (var message in scanMessages)
        {
            scan.AppendLine(message.Content);
        }

        scan.Append(request.UserInput);
        var scanText = scan.ToString();
        var active = new List<(EntryDefinition Entry, int Level)>();
        var activeIds = new HashSet<string>(StringComparer.Ordinal);
        var maximumSteps = Math.Clamp(request.MaximumRecursionSteps, 1, 20);
        for (var level = 0; level < maximumSteps; level++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var newlyActive = definitions
                .Where(entry => !activeIds.Contains(entry.Id))
                .Where(entry => (level == 0 && entry.Constant)
                                || Matches(entry, scanText))
                .Where(entry => PassesProbability(entry, request))
                .ToList();
            newlyActive = ResolveInclusionGroups(newlyActive);
            if (newlyActive.Count == 0)
            {
                break;
            }

            foreach (var entry in newlyActive)
            {
                activeIds.Add(entry.Id);
                active.Add((entry, level));
            }

            scanText += "\n" + string.Join(
                "\n",
                newlyActive.Select(entry => entry.Content));
        }

        var matches = new List<WorldbookMatch>();
        var usedCharacters = 0;
        foreach (var item in active
                     .OrderBy(value => value.Entry.InsertionOrder)
                     .ThenBy(value => value.Entry.OriginalIndex))
        {
            var expanded = _macros.Expand(
                item.Entry.Content,
                request.MacroVariables);
            if (expanded.Length == 0)
            {
                continue;
            }

            if (usedCharacters + expanded.Length > request.MaximumContentCharacters)
            {
                diagnostics.Add(
                    $"世界书条目“{item.Entry.Title}”因本轮世界书字符预算不足而未注入。");
                continue;
            }

            usedCharacters += expanded.Length;
            matches.Add(new WorldbookMatch(
                item.Entry.Id,
                item.Entry.Title,
                expanded,
                item.Entry.Position,
                item.Entry.Depth,
                item.Entry.ProviderRole,
                item.Entry.InsertionOrder,
                item.Level));
        }

        return Task.FromResult(new WorldbookScanResult(matches, diagnostics));
    }

    private static EntryDefinition? ReadEntry(
        JsonObject entry,
        int index,
        JsonObject book,
        ICollection<string> diagnostics)
    {
        if (ReadBoolean(entry, "enabled") == false)
        {
            return null;
        }

        var content = ReadString(entry, "content");
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var extensions = entry["extensions"] as JsonObject;
        var id = ReadString(entry, "id")
                 ?? ReadString(entry, "uid")
                 ?? index.ToString();
        var title = ReadString(entry, "name")
                    ?? ReadString(entry, "comment")
                    ?? $"角色世界书 · 条目 {index + 1}";
        var position = ReadPosition(entry, extensions, diagnostics, title);
        var role = (ReadString(entry, "role")
                    ?? ReadString(extensions, "role")
                    ?? "system").ToLowerInvariant();
        if (role is not ("system" or "user" or "assistant"))
        {
            diagnostics.Add($"世界书条目“{title}”的 role 无效，已按 system 处理。");
            role = "system";
        }

        return new EntryDefinition(
            id,
            title,
            content.Trim(),
            ReadStringArray(entry, "keys"),
            ReadStringArray(entry, "secondary_keys"),
            ReadBoolean(entry, "constant") == true,
            ReadBoolean(entry, "case_sensitive")
            ?? ReadBoolean(book, "case_sensitive")
            ?? false,
            ReadBoolean(entry, "match_whole_words")
            ?? ReadBoolean(extensions, "match_whole_words")
            ?? false,
            ReadLogic(entry, extensions),
            ReadInt32(entry, "insertion_order")
            ?? ReadInt32(entry, "order")
            ?? 100,
            position,
            Math.Clamp(
                ReadInt32(entry, "depth")
                ?? ReadInt32(extensions, "depth")
                ?? 4,
                1,
                100),
            role,
            Math.Clamp(
                ReadInt32(entry, "probability")
                ?? ReadInt32(extensions, "probability")
                ?? 100,
                0,
                100),
            ReadString(entry, "inclusion_group")
            ?? ReadString(entry, "group")
            ?? ReadString(extensions, "inclusion_group")
            ?? string.Empty,
            ReadInt32(entry, "group_weight")
            ?? ReadInt32(extensions, "group_weight")
            ?? 100,
            index);
    }

    private static bool Matches(EntryDefinition entry, string scanText)
    {
        var primary = entry.Keys.Count > 0
                      && entry.Keys.Any(key => KeyMatches(entry, key, scanText));
        if (!primary)
        {
            return false;
        }

        var secondaryMatches = entry.SecondaryKeys
            .Select(key => KeyMatches(entry, key, scanText))
            .ToArray();
        return entry.Logic switch
        {
            WorldbookSelectiveLogic.AndAny =>
                secondaryMatches.Length == 0 || secondaryMatches.Any(value => value),
            WorldbookSelectiveLogic.AndAll =>
                secondaryMatches.Length == 0 || secondaryMatches.All(value => value),
            WorldbookSelectiveLogic.NotAny =>
                secondaryMatches.All(value => !value),
            WorldbookSelectiveLogic.NotAll =>
                secondaryMatches.Length == 0 || !secondaryMatches.All(value => value),
            _ => true
        };
    }

    private static bool KeyMatches(
        EntryDefinition entry,
        string key,
        string scanText)
    {
        if (key.Length >= 2 && key[0] == '/' && key.LastIndexOf('/') > 0)
        {
            var lastSlash = key.LastIndexOf('/');
            var pattern = key[1..lastSlash];
            var flags = key[(lastSlash + 1)..];
            try
            {
                var options = RegexOptions.CultureInvariant;
                if (!entry.CaseSensitive || flags.Contains('i'))
                {
                    options |= RegexOptions.IgnoreCase;
                }

                return Regex.IsMatch(
                    scanText,
                    pattern,
                    options,
                    TimeSpan.FromMilliseconds(100));
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        if (!entry.MatchWholeWords)
        {
            return scanText.Contains(
                key,
                entry.CaseSensitive
                    ? StringComparison.Ordinal
                    : StringComparison.OrdinalIgnoreCase);
        }

        return Regex.IsMatch(
            scanText,
            $@"(?<![\p{{L}}\p{{N}}_]){Regex.Escape(key)}(?![\p{{L}}\p{{N}}_])",
            entry.CaseSensitive
                ? RegexOptions.CultureInvariant
                : RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(100));
    }

    private static List<EntryDefinition> ResolveInclusionGroups(
        IReadOnlyList<EntryDefinition> entries)
    {
        var withoutGroup = entries
            .Where(entry => string.IsNullOrWhiteSpace(entry.InclusionGroup))
            .ToList();
        foreach (var group in entries
                     .Where(entry => !string.IsNullOrWhiteSpace(entry.InclusionGroup))
                     .GroupBy(entry => entry.InclusionGroup, StringComparer.OrdinalIgnoreCase))
        {
            withoutGroup.Add(group
                .OrderByDescending(entry => entry.InsertionOrder)
                .ThenByDescending(entry => entry.GroupWeight)
                .ThenBy(entry => entry.OriginalIndex)
                .First());
        }

        return withoutGroup;
    }

    private static bool PassesProbability(
        EntryDefinition entry,
        WorldbookScanRequest request)
    {
        if (entry.Probability >= 100)
        {
            return true;
        }

        if (entry.Probability <= 0)
        {
            return false;
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{request.ConversationId}\0{request.UserInput}\0{entry.Id}"));
        return BitConverter.ToUInt32(bytes, 0) % 100 < entry.Probability;
    }

    private static WorldbookInsertionPosition ReadPosition(
        JsonObject entry,
        JsonObject? extensions,
        ICollection<string> diagnostics,
        string title)
    {
        var raw = ReadString(entry, "position")
                  ?? ReadString(extensions, "position");
        if (raw is not null)
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

        var numeric = ReadInt32(entry, "position")
                      ?? ReadInt32(extensions, "position");
        if (numeric is null)
        {
            return WorldbookInsertionPosition.AfterCharacter;
        }

        return numeric.Value switch
        {
            0 => WorldbookInsertionPosition.BeforeCharacter,
            1 => WorldbookInsertionPosition.AfterCharacter,
            4 or 6 => WorldbookInsertionPosition.HistoryDepth,
            _ => ReportUnsupportedPosition(diagnostics, title, numeric.Value)
        };
    }

    private static WorldbookInsertionPosition ReportUnsupportedPosition(
        ICollection<string> diagnostics,
        string title,
        int position)
    {
        diagnostics.Add(
            $"世界书条目“{title}”的位置 {position} 暂无独立槽位，已降级为角色设定后。");
        return WorldbookInsertionPosition.AfterCharacter;
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

        var numeric = ReadInt32(entry, "selectiveLogic")
                      ?? ReadInt32(entry, "selective_logic")
                      ?? ReadInt32(extensions, "selective_logic");
        return numeric switch
        {
            1 => WorldbookSelectiveLogic.AndAll,
            2 => WorldbookSelectiveLogic.NotAny,
            3 => WorldbookSelectiveLogic.NotAll,
            _ => WorldbookSelectiveLogic.AndAny
        };
    }

    private static JsonObject? ReadCardData(string rawJson)
    {
        try
        {
            var root = JsonNode.Parse(rawJson) as JsonObject;
            return root?["data"] as JsonObject ?? root;
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadString(JsonObject? source, string propertyName) =>
        source?[propertyName] is JsonValue value
        && value.TryGetValue<string>(out var result)
            ? result
            : null;

    private static bool? ReadBoolean(JsonObject? source, string propertyName) =>
        source?[propertyName] is JsonValue value
        && value.TryGetValue<bool>(out var result)
            ? result
            : null;

    private static int? ReadInt32(JsonObject? source, string propertyName) =>
        source?[propertyName] is JsonValue value
        && value.TryGetValue<int>(out var result)
            ? result
            : null;

    private static int? ReadExtensionInt32(JsonObject source, string propertyName) =>
        ReadInt32(source["extensions"] as JsonObject, propertyName);

    private static IReadOnlyList<string> ReadStringArray(
        JsonObject source,
        string propertyName) =>
        source[propertyName] is JsonArray array
            ? array
                .OfType<JsonValue>()
                .Select(value => value.TryGetValue<string>(out var result)
                    ? result.Trim()
                    : string.Empty)
                .Where(value => value.Length > 0)
                .ToArray()
            : Array.Empty<string>();

    private sealed record EntryDefinition(
        string Id,
        string Title,
        string Content,
        IReadOnlyList<string> Keys,
        IReadOnlyList<string> SecondaryKeys,
        bool Constant,
        bool CaseSensitive,
        bool MatchWholeWords,
        WorldbookSelectiveLogic Logic,
        int InsertionOrder,
        WorldbookInsertionPosition Position,
        int Depth,
        string ProviderRole,
        int Probability,
        string InclusionGroup,
        int GroupWeight,
        int OriginalIndex);
}
