using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;

namespace TavernDesk.Core.Flow;

/// <summary>
/// Resolves exactly one strategy for each supported campaign preset.  Requiring
/// all presets at construction prevents a silent legacy fallback from rejoining
/// the final architecture.
/// </summary>
public sealed class CampaignFlowStrategyRouter
{
    private readonly IReadOnlyDictionary<CampaignFlowPreset, ICampaignFlowStrategy>
        _strategies;

    public CampaignFlowStrategyRouter(
        IEnumerable<ICampaignFlowStrategy> strategies)
    {
        ArgumentNullException.ThrowIfNull(strategies);
        var materialized = strategies.ToArray();
        if (materialized.Any(item => item is null))
        {
            throw new ArgumentException(
                "Flow strategy collection cannot contain null entries.",
                nameof(strategies));
        }

        var duplicates = materialized
            .GroupBy(item => item.Preset)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicates.Length > 0)
        {
            throw new ArgumentException(
                $"Duplicate flow strategies: {string.Join(", ", duplicates)}.",
                nameof(strategies));
        }

        var missing = Enum.GetValues<CampaignFlowPreset>()
            .Except(materialized.Select(item => item.Preset))
            .ToArray();
        if (missing.Length > 0)
        {
            throw new ArgumentException(
                $"Missing flow strategies: {string.Join(", ", missing)}.",
                nameof(strategies));
        }

        _strategies = materialized.ToDictionary(item => item.Preset);
    }

    public ICampaignFlowStrategy Resolve(CampaignFlowPreset preset) =>
        _strategies.TryGetValue(preset, out var strategy)
            ? strategy
            : throw new InvalidOperationException(
                $"No campaign flow strategy is registered for {preset}.");
}
