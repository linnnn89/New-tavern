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
        // Reserve output and a safety margin before selecting any content. The
        // margin absorbs provider/tokenizer envelope differences rather than
        // pretending all of the advertised context window is usable input.
        var availableInput = Math.Max(
            0,
            contextLimit - reservedOutputTokens - safetyMargin);
        var envelopeTokens = EstimateEnvelope(contextLimit, modelId);
        var candidates = segments
            .Select((segment, index) => new Candidate(
                index,
                segment,
                Classify(segment),
                EstimateSegmentContribution(
                    segment,
                    contextLimit,
                    modelId,
                    envelopeTokens)))
            .ToArray();
        var historyBlocks = BuildHistoryBlocks(candidates);
        var latestHistoryBlock = historyBlocks.LastOrDefault();
        var stageAttachments = candidates
            .Where(candidate =>
                candidate.Tier == GroupContextBudgetTier.Dynamic
                && !string.IsNullOrWhiteSpace(candidate.Segment.HistoryBlockId))
            .GroupBy(
                candidate => candidate.Segment.HistoryBlockId!,
                StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(candidate => candidate.Index).ToArray(),
                StringComparer.Ordinal);
        var required = candidates
            .Where(candidate => candidate.Tier is GroupContextBudgetTier.Required
                or GroupContextBudgetTier.Strong)
            .ToList();
        if (latestHistoryBlock is not null)
        {
            required.AddRange(latestHistoryBlock.Segments);
        }

        // The newest complete history stage is part of the minimum reliable
        // request; omitting it can detach the current reply from the user turn it
        // is supposed to answer.
        var minimumRequired = EstimateExactSelection(
            required,
            contextLimit,
            modelId);
        if (minimumRequired > availableInput)
        {
            return BuildResult(
                candidates,
                required,
                latestHistoryBlock,
                contextLimit,
                reservedOutputTokens,
                safetyMargin,
                availableInput,
                minimumRequired,
                envelopeTokens,
                canSend: false,
                GroupContextBudgetStatus.ContextCapacityInsufficient,
                "当前请求的最低可靠上下文超过模型可用输入容量；未静默裁剪核心提示词、当前角色卡或当前用户输入。",
                minimumRequired);
        }

        var selected = new List<Candidate>(required);
        var selectedIndices = required
            .Select(candidate => candidate.Index)
            .ToHashSet();
        var usedTokens = minimumRequired;
        foreach (var candidate in candidates.Where(candidate =>
                     candidate.Tier == GroupContextBudgetTier.Dynamic
                     && string.IsNullOrWhiteSpace(
                         candidate.Segment.HistoryBlockId)))
        {
            if (!TryAddCandidate(
                    candidate,
                    selected,
                    selectedIndices,
                    availableInput,
                    contextLimit,
                    modelId,
                    out usedTokens))
            {
                continue;
            }
        }

        // Attachments follow their owning stage and remain best-effort: the stage
        // may fit even when an image/file payload does not.
        AddStageAttachments(
            latestHistoryBlock,
            stageAttachments,
            selected,
            selectedIndices,
            availableInput,
            contextLimit,
            modelId,
            ref usedTokens);

        foreach (var block in historyBlocks.AsEnumerable().Reverse())
        {
            if (ReferenceEquals(block, latestHistoryBlock))
            {
                continue;
            }

            var proposed = selected.Concat(block.Segments).ToArray();
            var proposedTokens = EstimateExactSelection(
                proposed,
                contextLimit,
                modelId);
            if (proposedTokens > availableInput)
            {
                // History is a continuous suffix of complete conversation
                // stages. Once the next older stage does not fit, do not skip
                // it and add an even older stage.
                break;
            }

            selected.AddRange(block.Segments);
            foreach (var candidate in block.Segments)
            {
                selectedIndices.Add(candidate.Index);
            }
            usedTokens = proposedTokens;
            AddStageAttachments(
                block,
                stageAttachments,
                selected,
                selectedIndices,
                availableInput,
                contextLimit,
                modelId,
                ref usedTokens);
        }

        var actualInput = EstimateExactSelection(
            selected,
            contextLimit,
            modelId);
        var status = selectedIndices.Count == candidates.Length
            ? GroupContextBudgetStatus.Safe
            : GroupContextBudgetStatus.Reduced;
        return BuildResult(
            candidates,
            selected,
            latestHistoryBlock,
            contextLimit,
            reservedOutputTokens,
            safetyMargin,
            availableInput,
            actualInput,
            envelopeTokens,
            canSend: true,
            status,
            failureReason: null,
            minimumRequired);
    }

    public static int CalculateSafetyMargin(int contextLimit) =>
        Math.Clamp(
            Math.Max(1, contextLimit) / 32,
            MinimumSafetyMarginTokens,
            MaximumSafetyMarginTokens);

    private GroupContextBudgetResult BuildResult(
        IReadOnlyList<Candidate> candidates,
        IReadOnlyCollection<Candidate> selected,
        HistoryBlock? latestHistoryBlock,
        int contextLimit,
        int reservedOutputTokens,
        int safetyMargin,
        int availableInput,
        int actualInput,
        int envelopeTokens,
        bool canSend,
        GroupContextBudgetStatus status,
        string? failureReason,
        int minimumRequired)
    {
        var selectedIndices = selected
            .Select(candidate => candidate.Index)
            .ToHashSet();
        var latestHistoryIndices = latestHistoryBlock is null
            ? []
            : latestHistoryBlock.Segments
                .Select(candidate => candidate.Index)
                .ToHashSet();
        var breakdown = candidates
            .Select(candidate =>
            {
                var isSelected = selectedIndices.Contains(candidate.Index);
                var minimumTokens = candidate.Tier switch
                {
                    GroupContextBudgetTier.Required
                        or GroupContextBudgetTier.Strong => candidate.TokenCost,
                    GroupContextBudgetTier.History
                        when latestHistoryIndices.Contains(candidate.Index) => candidate.TokenCost,
                    _ => 0
                };
                var reductionReason = isSelected
                    ? null
                    : candidate.Tier == GroupContextBudgetTier.History
                        ? "该完整发言阶段放不下；更旧阶段不会越过它单独加入。"
                        : "为最低可靠上下文和剩余预算让出空间。";
                return new GroupContextBudgetSegment(
                    candidate.Segment.Id,
                    candidate.Segment.Title,
                    candidate.Segment.Kind,
                    candidate.Tier,
                    candidate.TokenCost,
                    isSelected ? candidate.TokenCost : 0,
                    minimumTokens,
                    candidate.TokenCost,
                    candidate.TokenCost,
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
            minimumRequired,
            canSend,
            status,
            breakdown,
            selectedSegments,
            failureReason,
            envelopeTokens);
    }

    private int EstimateEnvelope(int contextLimit, string? modelId) =>
        _tokenEstimator.Estimate(
                Array.Empty<ContextSegment>(),
                contextLimit,
                0,
                modelId)
            .InputTokens;

    private int EstimateSegmentContribution(
        ContextSegment segment,
        int contextLimit,
        string? modelId,
        int envelopeTokens)
    {
        var single = _tokenEstimator.Estimate(
                [segment],
                contextLimit,
                0,
                modelId)
            .InputTokens;
        return Math.Max(0, single - envelopeTokens);
    }

    private int EstimateExactSelection(
        IEnumerable<Candidate> candidates,
        int contextLimit,
        string? modelId) =>
        _tokenEstimator.Estimate(
                candidates
                    .OrderBy(candidate => candidate.Index)
                    .Select(candidate => candidate.Segment),
                contextLimit,
                0,
                modelId)
            .InputTokens;

    private bool TryAddCandidate(
        Candidate candidate,
        ICollection<Candidate> selected,
        ISet<int> selectedIndices,
        int availableInput,
        int contextLimit,
        string? modelId,
        out int usedTokens)
    {
        // Re-estimate the complete message set after each addition because chat
        // envelopes and tokenizer boundaries make segment token costs non-additive.
        var proposed = selected.Append(candidate).ToArray();
        usedTokens = EstimateExactSelection(proposed, contextLimit, modelId);
        if (usedTokens > availableInput)
        {
            usedTokens = EstimateExactSelection(selected, contextLimit, modelId);
            return false;
        }

        selected.Add(candidate);
        selectedIndices.Add(candidate.Index);
        return true;
    }

    private void AddStageAttachments(
        HistoryBlock? block,
        IReadOnlyDictionary<string, Candidate[]> stageAttachments,
        ICollection<Candidate> selected,
        ISet<int> selectedIndices,
        int availableInput,
        int contextLimit,
        string? modelId,
        ref int usedTokens)
    {
        if (block is null
            || !stageAttachments.TryGetValue(block.Id, out var attachments))
        {
            return;
        }

        foreach (var attachment in attachments)
        {
            TryAddCandidate(
                attachment,
                selected,
                selectedIndices,
                availableInput,
                contextLimit,
                modelId,
                out usedTokens);
        }
    }

    private static IReadOnlyList<HistoryBlock> BuildHistoryBlocks(
        IReadOnlyList<Candidate> candidates)
    {
        return candidates
            .Where(candidate => candidate.Tier == GroupContextBudgetTier.History)
            .GroupBy(
                candidate => candidate.Segment.HistoryBlockId
                    ?? $"history-segment:{candidate.Index}",
                StringComparer.Ordinal)
            .Select(group =>
            {
                var blockSegments = group.OrderBy(item => item.Index).ToArray();
                return new HistoryBlock(
                    group.Key,
                    blockSegments,
                    blockSegments[0].Index,
                    blockSegments[^1].Index,
                    blockSegments.Sum(item => item.TokenCost));
            })
            .OrderBy(block => block.FirstIndex)
            .ToArray();
    }

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
        int TokenCost);

    private sealed record HistoryBlock(
        string Id,
        IReadOnlyList<Candidate> Segments,
        int FirstIndex,
        int LastIndex,
        int TokenCost);
}
