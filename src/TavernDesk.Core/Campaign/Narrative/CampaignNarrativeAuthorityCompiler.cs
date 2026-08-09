using System.Text.Json;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Narrative.Strategies;
using TavernDesk.Core.Models;

namespace TavernDesk.Core.Narrative;

public sealed class CampaignNarrativeAuthorityCompiler
    : ICampaignNarrativeAuthorityCompiler
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IReadOnlyDictionary<CampaignFlowPreset,
        ICampaignNarrativeAuthorityStrategy> _strategies;

    public CampaignNarrativeAuthorityCompiler(
        IEnumerable<ICampaignNarrativeAuthorityStrategy>? strategies = null)
    {
        var configured = strategies?.ToArray()
                         ??
                         [
                             new CollaborativeNarrativeAuthorityStrategy(),
                             new BlindSubmissionNarrativeAuthorityStrategy(),
                             new StrictInitiativeNarrativeAuthorityStrategy()
                         ];
        _strategies = configured.ToDictionary(item => item.Preset);
    }

    public CampaignNarrativeAuthority Compile(
        CampaignAggregate aggregate,
        CampaignResolutionPlan resolutionPlan,
        string directorInstructions)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        ArgumentNullException.ThrowIfNull(resolutionPlan);
        if (!_strategies.TryGetValue(
                aggregate.Campaign.FlowPreset,
                out var strategy))
        {
            throw new InvalidOperationException(
                $"缺少 {aggregate.Campaign.FlowPreset} 的叙事权限策略。");
        }

        var intentIds = resolutionPlan.PlayerIntentIds
            .ToHashSet(StringComparer.Ordinal);
        var activeIntents = aggregate.Events
            .Where(item => intentIds.Contains(item.Id))
            .Where(item => item.Kind == CampaignEventKind.PlayerIntent)
            .OrderBy(item => item.SequenceNo)
            .ToArray();
        var activeParticipantIds = activeIntents
            .Select(item => item.ActorId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var activeSet = activeParticipantIds.ToHashSet(StringComparer.Ordinal);
        var inactiveParticipantIds = aggregate.Participants
            .Where(item => item.IsEnabled && !activeSet.Contains(item.Id))
            .OrderBy(item => item.SortIndex)
            .Select(item => item.Id)
            .ToArray();
        var state = ParseState(aggregate.Campaign.NarrativeStateJson);
        state.RoundNo = aggregate.Campaign.CurrentRound;
        state.TurnIndex = aggregate.Campaign.CurrentTurnIndex;
        state.ActiveParticipantIds = [.. activeParticipantIds];

        return new CampaignNarrativeAuthority(
            aggregate.Campaign.FlowPreset,
            strategy.BuildModeContract(aggregate, activeIntents),
            directorInstructions.Trim(),
            aggregate.Campaign.NewNpcPermission,
            aggregate.Campaign.RelationshipChangePermission,
            aggregate.Campaign.IndependentPlotPermission,
            resolutionPlan.PlayerIntentIds.ToArray(),
            activeParticipantIds,
            inactiveParticipantIds,
            state);
    }

    public static string SerializeState(CampaignNarrativeState state) =>
        JsonSerializer.Serialize(state, JsonOptions);

    private static CampaignNarrativeState ParseState(string? json)
    {
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                return JsonSerializer.Deserialize<CampaignNarrativeState>(
                           json,
                           JsonOptions)
                       ?? new CampaignNarrativeState();
            }
            catch (JsonException)
            {
                // A damaged derived state must not prevent loading the ledger.
            }
        }

        return new CampaignNarrativeState();
    }
}
