using TavernDesk.Core.Abstractions;

namespace TavernDesk.Infrastructure.Context;

public sealed class DefaultContextBudgetProvider : IContextBudgetProvider
{
    private ContextBudget _budget = new(
        ContextLimit: 32768,
        ReservedOutputTokens: 4096,
        SourceLabel: "当前默认模型配置");

    public ContextBudget GetCurrentBudget() => Volatile.Read(ref _budget);

    public void UpdateBudget(ContextBudget budget)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(budget.ContextLimit, 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(budget.ReservedOutputTokens, 1);
        if (budget.ReservedOutputTokens > budget.ContextLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(budget),
                "预留输出不能超过上下文上限。");
        }

        Volatile.Write(ref _budget, budget);
    }
}
