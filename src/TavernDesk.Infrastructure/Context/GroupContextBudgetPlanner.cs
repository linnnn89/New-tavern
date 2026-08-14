using TavernDesk.Core.Abstractions;

namespace TavernDesk.Infrastructure.Context;

public sealed class GroupContextBudgetPlanner : IGroupContextBudgetPlanner
{
    private const int MinimumSafetyMarginTokens = 1_024;
    private const int MaximumSafetyMarginTokens = 4_096;

    private readonly ITokenEstimator _tokenEstimator;

    public GroupContextBudgetPlanner(ITokenEstimator tokenEstimator)
    {
        _tokenEstimator = tokenEstimator;
    }

    public GroupContextBudgetResult Plan(
        IReadOnlyList<ContextSegment> segments,
        int contextLimit,
        int reservedOutputTokens,
        string? modelId = null,
        int? safetyMarginTokens = null)
    {
        ArgumentNullException.ThrowIfNull(segments);
        contextLimit = Math.Max(1, contextLimit);
        reservedOutputTokens = Math.Max(0, reservedOutputTokens);
        var safetyMargin = Math.Max(
            0,
            safetyMarginTokens
                ?? CalculateSafetyMargin(contextLimit));
        var availableInput = Math.Max(
            0,
            contextLimit - reservedOutputTokens - safetyMargin);

        var candidates = segments
            .Select((segment, index) => new Candidate(
                index,
                segment,
                Classify(segment),
                EstimateSegment(segment, contextLimit, modelId)))
            .ToArray();
        var latestHistoryIndex = candidates
            .Where(candidate => candidate.Tier == GroupContextBudgetTier.History)
            .Select(candidate => (int?)candidate.Index)
            .LastOrDefault();
        var required = candidates
            .Where(candidate => candidate.Tier is GroupContextBudgetTier.Required
                or GroupContextBudgetTier.Strong
                || candidate.Index == latestHistoryIndex)
            .ToArray();
        var selected = new List<Candidate>(required);
        var minimumRequired = EstimateSelection(
            selected,
            contextLimit,
            reservedOutputTokens,
            modelId);
        if (minimumRequired > availableInput)
        {
            return BuildResult(
                candidates,
                selected,
                contextLimit,
                reservedOutputTokens,
                safetyMargin,
                availableInput,
                minimumRequired,
                canSend: false,
                GroupContextBudgetStatus.ContextCapacityInsufficient,
                "当前请求的最低可靠上下文超过模型可用输入容量；未静默裁剪核心提示词、当前角色卡或当前用户输入。",
                modelId);
        }

        foreach (var candidate in candidates.Where(candidate =>
                     candidate.Tier == GroupContextBudgetTier.Dynamic))
        {
            TryAdd(candidate, selected, availableInput, contextLimit, reservedOutputTokens, modelId);
        }

        foreach (var candidate in candidates
                     .Where(candidate => candidate.Tier == GroupContextBudgetTier.History)
                     .OrderByDescending(candidate => candidate.Index))
        {
            TryAdd(candidate, selected, availableInput, contextLimit, reservedOutputTokens, modelId);
        }

        var actualInput = EstimateSelection(
            selected,
            contextLimit,
            reservedOutputTokens,
            modelId);
        var selectedIndices = selected
            .Select(candidate => candidate.Index)
            .ToHashSet();
        var status = selectedIndices.Count == candidates.Length
            ? GroupContextBudgetStatus.Safe
            : GroupContextBudgetStatus.Reduced;
        return BuildResult(
            candidates,
            selected,
            contextLimit,
            reservedOutputTokens,
            safetyMargin,
            availableInput,
            actualInput,
            canSend: true,
            status,
            FailureReason: null,
            modelId);
    }

    public static int CalculateSafetyMargin(int contextLimit) =>
        Math.Clamp(
            Math.Max(1, contextLimit) / 32,
            MinimumSafetyMarginTokens,
            MaximumSafetyMarginTokens);

    private void TryAdd(
        Candidate candidate,
        ICollection<Candidate> selected,
        int availableInput,
        int contextLimit,
        int reservedOutputTokens,
        string? modelId)
    {
        if (selected.Any(item => item.Index == candidate.Index))
        {
            return;
        }

        var proposed = selected.Append(candidate).ToArray();
        if (EstimateSelection(
                proposed,
                contextLimit,
                reservedOutputTokens,
                modelId) <= availableInput)
        {
            selected.Add(candidate);
        }
    }

    private GroupContextBudgetResult BuildResult(
        IReadOnlyList<Candidate> candidates,
        IReadOnlyCollection<Candidate> selected,
        int contextLimit,
        int reservedOutputTokens,
        int safetyMargin,
        int availableInput,
        int actualInput,
        bool canSend,
        GroupContextBudgetStatus status,
        string? FailureReason,
        string? modelId)
    {
        var selectedIndices = selected
            .Select(candidate => candidate.Index)
            .ToHashSet();
        var latestHistoryIndex = candidates
            .Where(candidate => candidate.Tier == GroupContextBudgetTier.History)
            .Select(candidate => (int?)candidate.Index)
            .LastOrDefault();
        var breakdown = candidates
            .Select(candidate =>
            {
                var isSelected = selectedIndices.Contains(candidate.Index);
                var minimumTokens = candidate.Tier switch
                {
                    GroupContextBudgetTier.Required
                        or GroupContextBudgetTier.Strong => candidate.OriginalTokens,
                    GroupContextBudgetTier.History
                        when candidate.Index == latestHistoryIndex => candidate.OriginalTokens,
                    _ => 0
                };
                var reductionReason = isSelected
                    ? null
                    : candidate.Tier == GroupContextBudgetTier.History
                        ? "近期历史按完整消息边界从旧到新缩减。"
                        : "为最低可靠上下文和剩余预算让出空间。";
                return new GroupContextBudgetSegment(
                    candidate.Segment.Id,
                    candidate.Segment.Title,
                    candidate.Segment.Kind,
                    candidate.Tier,
                    candidate.OriginalTokens,
                    isSelected ? candidate.OriginalTokens : 0,
                    minimumTokens,
                    candidate.OriginalTokens,
                    candidate.OriginalTokens,
                    WasReduced: !isSelected,
                    reductionReason);
            })
            .ToArray();
        var selectedSegments = candidates
            .Where(candidate => selectedIndices.Contains(candidate.Index))
            .Select(candidate => candidate.Segment)
            .ToArray();
        return new GroupContextBudgetResult(
            contextLimit,
            reservedOutputTokens,
            safetyMargin,
            availableInput,
            actualInput,
            Math.Max(0, availableInput - actualInput),
            MinimumRequiredTokens(candidates, contextLimit, reservedOutputTokens, modelId),
            canSend,
            status,
            breakdown,
            selectedSegments,
            FailureReason);
    }

    private int MinimumRequiredTokens(
        IReadOnlyList<Candidate> candidates,
        int contextLimit,
        int reservedOutputTokens,
        string? modelId)
    {
        var latestHistoryIndex = candidates
            .Where(candidate => candidate.Tier == GroupContextBudgetTier.History)
            .Select(candidate => (int?)candidate.Index)
            .LastOrDefault();
        return EstimateSelection(
            candidates
                .Where(candidate => candidate.Tier is GroupContextBudgetTier.Required
                    or GroupContextBudgetTier.Strong
                    || candidate.Index == latestHistoryIndex),
            contextLimit,
            reservedOutputTokens,
            modelId);
    }

    private int EstimateSelection(
        IEnumerable<Candidate> candidates,
        int contextLimit,
        int reservedOutputTokens,
        string? modelId) =>
        _tokenEstimator.Estimate(
                candidates
                    .OrderBy(candidate => candidate.Index)
                    .Select(candidate => candidate.Segment),
                contextLimit,
                reservedOutputTokens,
                modelId)
            .InputTokens;

    private int EstimateSegment(
        ContextSegment segment,
        int contextLimit,
        string? modelId) =>
        _tokenEstimator.Estimate(
                [segment],
                contextLimit,
                0,
                modelId)
            .InputTokens;

    private static GroupContextBudgetTier Classify(ContextSegment segment)
    {
        if (segment.Kind == ContextSegmentKind.History)
        {
            return GroupContextBudgetTier.History;
        }

        if (segment.Id.StartsWith("group-roster:", StringComparison.Ordinal)
            || segment.Kind is ContextSegmentKind.Worldbook
                or ContextSegmentKind.Knowledge
                or ContextSegmentKind.Search)
        {
            return GroupContextBudgetTier.Dynamic;
        }

        if (segment.Kind == ContextSegmentKind.Memory)
        {
            return GroupContextBudgetTier.Strong;
        }

        return GroupContextBudgetTier.Required;
    }

    private sealed record Candidate(
        int Index,
        ContextSegment Segment,
        GroupContextBudgetTier Tier,
        int OriginalTokens);
}
