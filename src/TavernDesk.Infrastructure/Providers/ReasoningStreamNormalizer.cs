using System.Text;
using System.Text.Json;

namespace TavernDesk.Infrastructure.Providers;

/// <summary>
/// Normalizes provider-specific reasoning shapes without coupling the caller to
/// a provider brand. Structured fields win; paired text tags are only treated
/// as reasoning when they occur at the very start of an otherwise plain stream.
/// </summary>
internal sealed class ReasoningStreamNormalizer
{
    // These are semantic aliases observed across OpenAI-compatible servers.
    // Adding a future alias changes one declaration instead of every adapter.
    private static readonly HashSet<string> ReasoningFieldAliases =
        new(
            ["reasoning", "reasoning_content", "thinking", "analysis"],
            StringComparer.OrdinalIgnoreCase);

    private static readonly ReasoningTagPair[] ReasoningTagPairs =
    [
        new("<think>", "</think>"),
        new("<thinking>", "</thinking>"),
        new("<analysis>", "</analysis>")
    ];

    private readonly LeadingReasoningTagFilter _tagFilter =
        new(ReasoningTagPairs);
    private bool _structuredReasoningSeen;

    public NormalizedReasoningChunk Push(
        string content,
        JsonElement structuredContainer)
    {
        var hasStructuredReasoning = HasStructuredReasoning(structuredContainer);
        _structuredReasoningSeen |= hasStructuredReasoning;
        if (_structuredReasoningSeen)
        {
            return new NormalizedReasoningChunk(
                hasStructuredReasoning,
                content);
        }

        return _tagFilter.Push(content);
    }

    public NormalizedReasoningChunk Complete() =>
        _structuredReasoningSeen
            ? new NormalizedReasoningChunk(false, string.Empty)
            : _tagFilter.Complete();

    private static bool HasStructuredReasoning(JsonElement container)
    {
        if (container.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in container.EnumerateObject())
        {
            if (IsReasoningField(property.Name)
                && HasMeaningfulValue(property.Value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsReasoningField(string propertyName) =>
        ReasoningFieldAliases.Contains(propertyName)
        // Controlled wildcard support covers names such as reasoning_text or
        // thinkingContent while avoiding an unrestricted recursive regex.
        || propertyName.StartsWith(
            "reasoning",
            StringComparison.OrdinalIgnoreCase)
        || propertyName.StartsWith(
            "thinking",
            StringComparison.OrdinalIgnoreCase);

    private static bool HasMeaningfulValue(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
            JsonValueKind.Array => value.EnumerateArray()
                .Any(HasMeaningfulValue),
            JsonValueKind.Object => value.EnumerateObject()
                .Any(property => HasMeaningfulValue(property.Value)),
            JsonValueKind.Number or JsonValueKind.True => true,
            _ => false
        };

    private sealed class LeadingReasoningTagFilter
    {
        private const int MaximumLeadingWhitespace = 256;
        private readonly IReadOnlyList<ReasoningTagPair> _pairs;
        private readonly StringBuilder _pending = new();
        private FilterState _state = FilterState.Detecting;
        private ReasoningTagPair? _activePair;

        public LeadingReasoningTagFilter(IReadOnlyList<ReasoningTagPair> pairs)
        {
            _pairs = pairs;
        }

        public NormalizedReasoningChunk Push(string content)
        {
            if (content.Length == 0)
            {
                return new NormalizedReasoningChunk(false, string.Empty);
            }

            if (_state == FilterState.Content)
            {
                return new NormalizedReasoningChunk(false, content);
            }

            _pending.Append(content);
            return _state == FilterState.Detecting
                ? DetectOpeningTag()
                : ConsumeReasoning();
        }

        public NormalizedReasoningChunk Complete()
        {
            if (_state == FilterState.Detecting)
            {
                var content = _pending.ToString();
                _pending.Clear();
                _state = FilterState.Content;
                return new NormalizedReasoningChunk(false, content);
            }

            return new NormalizedReasoningChunk(
                _state == FilterState.Reasoning,
                string.Empty);
        }

        private NormalizedReasoningChunk DetectOpeningTag()
        {
            var buffered = _pending.ToString();
            var leadingWhitespace = 0;
            while (leadingWhitespace < buffered.Length
                   && char.IsWhiteSpace(buffered[leadingWhitespace]))
            {
                leadingWhitespace++;
            }

            if (leadingWhitespace > MaximumLeadingWhitespace)
            {
                return ReleaseAsContent();
            }

            var candidate = buffered[leadingWhitespace..];
            foreach (var pair in _pairs)
            {
                if (candidate.StartsWith(pair.Start, StringComparison.OrdinalIgnoreCase))
                {
                    _activePair = pair;
                    _state = FilterState.Reasoning;
                    _pending.Clear();
                    _pending.Append(candidate[pair.Start.Length..]);
                    return ConsumeReasoning();
                }
            }

            if (candidate.Length == 0
                || _pairs.Any(pair =>
                    pair.Start.StartsWith(candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return new NormalizedReasoningChunk(false, string.Empty);
            }

            return ReleaseAsContent();
        }

        private NormalizedReasoningChunk ConsumeReasoning()
        {
            var pair = _activePair
                       ?? throw new InvalidOperationException("思考标签状态缺少结束标记。");
            var buffered = _pending.ToString();
            var closingIndex = buffered.IndexOf(
                pair.End,
                StringComparison.OrdinalIgnoreCase);
            if (closingIndex >= 0)
            {
                var content = buffered[(closingIndex + pair.End.Length)..];
                _pending.Clear();
                _state = FilterState.Content;
                return new NormalizedReasoningChunk(true, content);
            }

            // Retain only a suffix that could become a split closing tag in the
            // next SSE chunk; arbitrarily long reasoning is never buffered.
            var suffixLength = LongestClosingTagPrefixSuffix(buffered, pair.End);
            _pending.Clear();
            if (suffixLength > 0)
            {
                _pending.Append(buffered[^suffixLength..]);
            }

            return new NormalizedReasoningChunk(true, string.Empty);
        }

        private NormalizedReasoningChunk ReleaseAsContent()
        {
            var content = _pending.ToString();
            _pending.Clear();
            _state = FilterState.Content;
            return new NormalizedReasoningChunk(false, content);
        }

        private static int LongestClosingTagPrefixSuffix(
            string value,
            string closingTag)
        {
            var maximum = Math.Min(value.Length, closingTag.Length - 1);
            for (var length = maximum; length > 0; length--)
            {
                if (value.EndsWith(
                        closingTag[..length],
                        StringComparison.OrdinalIgnoreCase))
                {
                    return length;
                }
            }

            return 0;
        }

        private enum FilterState
        {
            Detecting,
            Reasoning,
            Content
        }
    }

    private sealed record ReasoningTagPair(string Start, string End);
}

internal readonly record struct NormalizedReasoningChunk(
    bool HasReasoning,
    string Content);
