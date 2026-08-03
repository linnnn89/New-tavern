using System.Text;
using Microsoft.ML.Tokenizers;
using TavernDesk.Core.Abstractions;

namespace TavernDesk.Infrastructure.Context;

public sealed class ModelAwareTokenEstimator : ITokenEstimator
{
    private static readonly Lazy<Tokenizer> O200kTokenizer = new(
        () => TiktokenTokenizer.CreateForEncoding("o200k_base"));
    private static readonly Lazy<Tokenizer> Cl100kTokenizer = new(
        () => TiktokenTokenizer.CreateForEncoding("cl100k_base"));

    public TokenEstimate Estimate(
        IEnumerable<ContextSegment> segments,
        int contextLimit,
        int reservedOutputTokens,
        string? modelId = null)
    {
        var materializedSegments = segments as IReadOnlyCollection<ContextSegment>
            ?? segments.ToArray();
        var tokenizer = ResolveTokenizer(modelId);
        var inputTokens = tokenizer is null
            ? EstimateHeuristically(materializedSegments)
            : EstimateTiktokenMessages(materializedSegments, tokenizer);

        return new TokenEstimate(
            InputTokens: Math.Max(0, inputTokens),
            ReservedOutputTokens: Math.Max(0, reservedOutputTokens),
            ContextLimit: Math.Max(1, contextLimit),
            IsExact: false);
    }

    private static int EstimateTiktokenMessages(
        IEnumerable<ContextSegment> segments,
        Tokenizer tokenizer)
    {
        var inputTokens = 3;
        foreach (var segment in segments)
        {
            inputTokens += 3;
            inputTokens += tokenizer.CountTokens(segment.ProviderRole);
            inputTokens += tokenizer.CountTokens(
                segment.ProviderContent ?? segment.Content);
        }

        return inputTokens;
    }

    private static int EstimateHeuristically(
        IEnumerable<ContextSegment> segments)
    {
        var inputTokens = 0;
        foreach (var segment in segments)
        {
            var byteCount = Encoding.UTF8.GetByteCount(
                segment.ProviderContent ?? segment.Content);
            inputTokens += (int)Math.Ceiling(byteCount / 3.2d) + 4;
        }

        return inputTokens;
    }

    private static Tokenizer? ResolveTokenizer(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return null;
        }

        var normalized = modelId
            .Trim()
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault()?
            .ToLowerInvariant();
        if (normalized is null)
        {
            return null;
        }

        if (normalized.StartsWith("gpt-5", StringComparison.Ordinal)
            || normalized.StartsWith("gpt-4.1", StringComparison.Ordinal)
            || normalized.StartsWith("gpt-4o", StringComparison.Ordinal)
            || normalized.StartsWith("o1", StringComparison.Ordinal)
            || normalized.StartsWith("o3", StringComparison.Ordinal)
            || normalized.StartsWith("o4", StringComparison.Ordinal))
        {
            return O200kTokenizer.Value;
        }

        if (normalized.StartsWith("gpt-4", StringComparison.Ordinal)
            || normalized.StartsWith("gpt-3.5", StringComparison.Ordinal)
            || normalized.StartsWith("gpt-35", StringComparison.Ordinal))
        {
            return Cl100kTokenizer.Value;
        }

        return null;
    }
}
