using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;
using TavernDesk.Infrastructure.Worldbooks;

namespace TavernDesk.Infrastructure.Context;

public sealed class CharacterWorldbookEngine : IWorldbookEngine
{
    private const int MaximumCachedDocuments = 64;
    private readonly IMacroEngine _macros;
    private readonly ConcurrentDictionary<string, ParsedWorldbookDocument> _documentCache =
        new(StringComparer.Ordinal);

    public CharacterWorldbookEngine(IMacroEngine macros)
    {
        _macros = macros;
    }

    public Task<WorldbookScanResult> ScanAsync(
        WorldbookScanRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sources = new List<(string RawJson, string IdPrefix)>
        {
            (request.RawCardJson, string.Empty)
        };
        if (request.AdditionalRawBookJson is { Count: > 0 })
        {
            sources.AddRange(request.AdditionalRawBookJson
                .Where(raw => !string.IsNullOrWhiteSpace(raw))
                .Select((raw, index) => (raw, $"book-{index + 1}:")));
        }

        var diagnostics = new List<string>();
        var matches = new List<WorldbookMatch>();
        var usedCharacters = 0;
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = ParseCached(source.RawJson);
            diagnostics.AddRange(document.Diagnostics);
            if (!document.FoundBook)
            {
                continue;
            }

            var definitions = document.Entries
                .Where(entry => entry.Enabled)
                .Select(entry => ToDefinition(entry, source.IdPrefix))
                .ToArray();
            var scanDepth = Math.Clamp(
                document.ScanDepth,
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
            var maximumSteps = document.RecursiveScanning
                ? Math.Clamp(request.MaximumRecursionSteps, 1, 20)
                : 1;
            // Recursive scanning feeds newly activated entry content into the next
            // pass. activeIds guarantees termination and ExcludeRecursion keeps
            // entries intended only for direct user/history matches out of it.
            for (var level = 0; level < maximumSteps; level++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var newlyActive = definitions
                    .Where(entry => !activeIds.Contains(entry.Id))
                    .Where(entry => level == 0 || !entry.ExcludeRecursion)
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
                    item.Level,
                    ContentType: item.Entry.ContentType));
            }
        }

        return Task.FromResult(new WorldbookScanResult(matches, diagnostics));
    }

    private ParsedWorldbookDocument ParseCached(string rawJson)
    {
        if (_documentCache.TryGetValue(rawJson, out var cached))
        {
            return cached;
        }

        var parsed = WorldbookJsonParser.Parse(rawJson);
        // Raw card JSON is immutable for a scan, so it is a safe cache key. A
        // small clear-all cap avoids retaining many old edited card versions and
        // needs no eviction coordination across concurrent scans.
        if (_documentCache.Count >= MaximumCachedDocuments)
        {
            _documentCache.Clear();
        }

        return _documentCache.GetOrAdd(rawJson, parsed);
    }

    private static EntryDefinition ToDefinition(
        WorldbookEntry entry,
        string idPrefix) =>
        new(
            idPrefix + entry.Id,
            entry.Title,
            entry.Content,
            entry.Keys,
            entry.SecondaryKeys,
            entry.Constant,
            entry.CaseSensitive,
            entry.MatchWholeWords,
            entry.SelectiveLogic,
            entry.InsertionOrder,
            entry.Position,
            entry.Depth,
            entry.ProviderRole,
            entry.Probability,
            entry.UseProbability,
            entry.InclusionGroup,
            entry.GroupWeight,
            entry.ExcludeRecursion,
            entry.OriginalIndex,
            entry.ContentType);

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

                // Imported regexes are untrusted; the timeout prevents one worldbook
                // key from stalling the complete context assembly pipeline.
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
        if (!entry.UseProbability || entry.Probability >= 100)
        {
            return true;
        }

        if (entry.Probability <= 0)
        {
            return false;
        }

        // Probability is deterministic for the same conversation/input/entry.
        // Context previews and the eventual send therefore activate the same lore.
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{request.ConversationId}\0{request.UserInput}\0{entry.Id}"));
        return BitConverter.ToUInt32(bytes, 0) % 100 < entry.Probability;
    }

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
        bool UseProbability,
        string InclusionGroup,
        int GroupWeight,
        bool ExcludeRecursion,
        int OriginalIndex,
        WorldbookContentType ContentType);
}
