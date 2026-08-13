namespace TavernDesk.Core.Abstractions;

public enum GroupContextBudgetTier
{
    Required = 0,
    Strong = 1,
    Dynamic = 2,
    History = 3
}

public enum GroupContextBudgetStatus
{
    Safe,
    Reduced,
    ContextCapacityInsufficient
}

public sealed record GroupContextBudgetSegment(
    string Id,
    string Name,
    ContextSegmentKind Kind,
    GroupContextBudgetTier Tier,
    int OriginalTokens,
    int AllocatedTokens,
    int MinimumTokens,
    int PreferredTokens,
    int MaximumTokens,
    bool WasReduced,
    string? ReductionReason = null);

public sealed record GroupContextBudgetResult(
    int ContextLimit,
    int ReservedOutputTokens,
    int SafetyMarginTokens,
    int AvailableInputTokens,
    int ActualInputTokens,
    int RemainingTokens,
    int MinimumRequiredTokens,
    bool CanSend,
    GroupContextBudgetStatus Status,
    IReadOnlyList<GroupContextBudgetSegment> Segments,
    IReadOnlyList<ContextSegment> SelectedSegments,
    string? FailureReason = null);

public interface IGroupContextBudgetPlanner
{
    GroupContextBudgetResult Plan(
        IReadOnlyList<ContextSegment> segments,
        int contextLimit,
        int reservedOutputTokens,
        string? modelId = null,
        int? safetyMarginTokens = null);
}
