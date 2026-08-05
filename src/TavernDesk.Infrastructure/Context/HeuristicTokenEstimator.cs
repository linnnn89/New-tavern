using System.Text;
using TavernDesk.Core.Abstractions;

namespace TavernDesk.Infrastructure.Context;

public sealed class HeuristicTokenEstimator : ITokenEstimator
{
    public TokenEstimate Estimate(
        IEnumerable<ContextSegment> segments,
        int contextLimit,
        int reservedOutputTokens,
        string? modelId = null)
    {
        var inputTokens = 0;
        foreach (var segment in segments)
        {
            var byteCount = Encoding.UTF8.GetByteCount(segment.Content);
            inputTokens += (int)Math.Ceiling(byteCount / 3.2d) + 4;
        }

        return new TokenEstimate(
            InputTokens: Math.Max(0, inputTokens),
            ReservedOutputTokens: Math.Max(0, reservedOutputTokens),
            ContextLimit: Math.Max(1, contextLimit),
            IsExact: false);
    }
}
