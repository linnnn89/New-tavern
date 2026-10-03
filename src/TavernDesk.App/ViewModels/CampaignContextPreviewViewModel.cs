using System.Collections.ObjectModel;
using TavernDesk.App.Localization;
using TavernDesk.App.Presentation;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Flow;
using TavernDesk.Core.Models;

namespace TavernDesk.App.ViewModels;

public sealed class CampaignContextPreviewViewModel : ViewModelBase
{
    private readonly ICampaignContextPlanner? _campaignContextPlanner;
    private readonly ICampaignScenarioRepository _scenarios;
    private readonly ICampaignMemoryRepository? _campaignMemories;
    private readonly ICampaignFlowEngine _flowEngine;
    private readonly Func<CampaignAggregate, bool> _isCurrentGame;
    private readonly Action _onPreviewChanged;
    private readonly Dictionary<string, string> _seatReasons = new(StringComparer.Ordinal);
    private string _summary = LanguageRuntime.GetString("Campaigns.ContextPreview.Hint");
    private string? _blockingReason;

    public CampaignContextPreviewViewModel(
        ICampaignContextPlanner? planner,
        ICampaignScenarioRepository scenarios,
        ICampaignMemoryRepository? memories,
        ICampaignFlowEngine flowEngine,
        Func<CampaignAggregate, bool> isCurrentGame,
        Action onPreviewChanged)
    {
        _campaignContextPlanner = planner;
        _scenarios = scenarios;
        _campaignMemories = memories;
        _flowEngine = flowEngine;
        _isCurrentGame = isCurrentGame;
        _onPreviewChanged = onPreviewChanged;
    }

    public ObservableCollection<CampaignContextPreviewItemViewModel> ContextPreviewItems { get; } = [];
    public string ContextPreviewSummary => _summary;
    public bool HasContextPreview => ContextPreviewItems.Count > 0;
    public bool IsBlocked { get; private set; }
    public string BlockedHelpText => string.IsNullOrWhiteSpace(_blockingReason)
        ? LanguageRuntime.GetString("Campaigns.Context.Blocked")
        : LanguageRuntime.Format("Campaigns.Context.BlockedFormat", _blockingReason);
    public bool TryGetSeatBlockReason(string seatId, out string? reason) =>
        _seatReasons.TryGetValue(seatId, out reason);

    public async Task RefreshAsync(CampaignAggregate game)
    {
        if (!_isCurrentGame(game)) return;
        ContextPreviewItems.Clear();
        _seatReasons.Clear();
        IsBlocked = false;
        _blockingReason = null;
        _summary = LanguageRuntime.GetString("Campaigns.ContextPreview.Hint");
        OnPropertyChanged(nameof(HasContextPreview));
        OnPropertyChanged(nameof(ContextPreviewSummary));
        var items = new List<CampaignContextPreviewItemViewModel>();
        var seatReasons = new Dictionary<string, string>(StringComparer.Ordinal);
        var blocked = false;
        string? blockingReason = null;
        var summary = LanguageRuntime.GetString("Campaigns.ContextPreview.Hint");
        if (_campaignContextPlanner is null)
        {
            Apply(items, seatReasons, blocked, blockingReason, summary);
            return;
        }
        var campaignId = game.Campaign.Id;
        try
        {
            if (game.Campaign.Phase == CampaignPhase.ReadyForResolution
                && game.Campaign.GmKind == CampaignGmKind.Ai)
            {
                var scenarioTask = _scenarios.GetAsync(game.Campaign.StoryId);
                var memoryTask = game.Campaign.MemoryEnabled
                    ? _campaignMemories?.GetBankAsync(
                        campaignId,
                        CampaignMemoryScope.GameMaster)
                      ?? Task.FromResult<CampaignMemoryBank?>(null)
                    : Task.FromResult<CampaignMemoryBank?>(null);
                await Task.WhenAll(scenarioTask, memoryTask);
                var plan = await _campaignContextPlanner.BuildGmPlanAsync(
                    game,
                    _flowEngine.PlanResolution(game),
                    scenarioTask.Result,
                    memoryTask.Result,
                    includeLongTermMemory: game.Campaign.MemoryEnabled);
                items.Add(CreateContextPreviewItem("AI GM", plan));
                blocked = plan.Status == CampaignContextPlanStatus.BlockedMandatoryContextTooLarge;
                blockingReason = blocked ? ContextBlockReason(plan) : null;
                summary = LanguageRuntime.Format(
                    "Campaigns.ContextPreview.GmFormat",
                    plan.Estimate.InputTokens,
                    EffectiveInputBudget(plan),
                    plan.Estimate.ReservedOutputTokens);
            }
            else if (game.Campaign.Phase == CampaignPhase.AwaitingActions)
            {
                var memory = !game.Campaign.MemoryEnabled || _campaignMemories is null
                    ? null
                    : await _campaignMemories.GetBankAsync(
                        campaignId,
                        CampaignMemoryScope.Public);
                var aiParticipants = game.Participants
                    .Where(item => item.IsEnabled
                                   && item.Kind == CampaignParticipantKind.Ai)
                    .OrderBy(item => item.SortIndex)
                    .ToArray();
                var plans = await Task.WhenAll(aiParticipants.Select(
                    participant =>
                        _campaignContextPlanner.BuildPlayerPlanAsync(
                            game,
                            participant,
                            memory,
                            includeLongTermMemory: game.Campaign.MemoryEnabled)));
                for (var index = 0; index < aiParticipants.Length; index++)
                {
                    items.Add(CreateContextPreviewItem(
                        aiParticipants[index].DisplayName,
                        plans[index]));
                    if (plans[index].Status
                        == CampaignContextPlanStatus.BlockedMandatoryContextTooLarge)
                    {
                        seatReasons[aiParticipants[index].Id] =
                            ContextBlockReason(plans[index]);
                    }
                }
                blocked = plans.Any(plan =>
                    plan.Status
                    == CampaignContextPlanStatus.BlockedMandatoryContextTooLarge);
                blockingReason = plans
                    .Where(plan => plan.Status
                                   == CampaignContextPlanStatus.BlockedMandatoryContextTooLarge)
                    .Select(ContextBlockReason)
                    .FirstOrDefault();

                if (_flowEngine.Inspect(game).ActionPlan.ExecutionMode
                    == CampaignActionExecutionMode.Parallel)
                {
                    summary = LanguageRuntime.Format(
                        "Campaigns.ContextPreview.BlindCostFormat",
                        plans.Length,
                        plans.Sum(plan => plan.Estimate.InputTokens),
                        plans.Sum(plan => plan.Estimate.ReservedOutputTokens));
                }
                else
                {
                    summary = LanguageRuntime.Format(
                        "Campaigns.ContextPreview.SeatsFormat",
                        plans.Length);
                }
            }

        }
        catch (Exception exception)
        {
            items.Clear(); seatReasons.Clear(); blocked = false; blockingReason = null;
            summary = LanguageRuntime.Format(
                "Campaigns.ContextPreview.UnavailableFormat", LanguageRuntime.ErrorMessage(exception));
        }
        // A completed plan belongs to the aggregate captured before awaiting.
        if (!_isCurrentGame(game)) return;
        Apply(items, seatReasons, blocked, blockingReason, summary);
    }

    private void Apply(
        IReadOnlyList<CampaignContextPreviewItemViewModel> items,
        IReadOnlyDictionary<string, string> seatReasons,
        bool blocked, string? blockingReason, string summary)
    {
        ContextPreviewItems.Clear();
        foreach (var item in items) ContextPreviewItems.Add(item);
        _seatReasons.Clear();
        foreach (var pair in seatReasons) _seatReasons.Add(pair.Key, pair.Value);
        IsBlocked = blocked;
        _blockingReason = blockingReason;
        _summary = summary;
        OnPropertyChanged(nameof(HasContextPreview));
        OnPropertyChanged(nameof(ContextPreviewSummary));
        OnPropertyChanged(nameof(IsBlocked));
        OnPropertyChanged(nameof(BlockedHelpText));
        _onPreviewChanged();
    }

    private static CampaignContextPreviewItemViewModel CreateContextPreviewItem(
        string title,
        CampaignContextPlan plan)
    {
        var status = ContextPlanStatusText(plan);
        var sections = plan.Sections
            .Where(section => section.IsMandatory
                             || section.EstimatedTokens > 0
                             || (section.Kind == ContextSegmentKind.Memory
                                 && !section.WasIncluded
                                 && !section.WasTruncated))
            .Select(section => new CampaignContextSectionItemViewModel(
                ContextSectionTitle(section),
                $"{section.EstimatedTokens:N0} tokens",
                ContextSectionStateText(section)))
            .ToArray();
        return new CampaignContextPreviewItemViewModel(
            title,
            LanguageRuntime.Format(
                "Campaigns.ContextPreview.ItemFormat",
                plan.Estimate.InputTokens,
                EffectiveInputBudget(plan),
                plan.Estimate.ReservedOutputTokens),
            status,
            sections);
    }

    private static int EffectiveInputBudget(CampaignContextPlan plan) =>
        Math.Max(
            0,
            plan.Estimate.ContextLimit
            - plan.Estimate.ReservedOutputTokens);

    private static string ContextPlanStatusText(CampaignContextPlan plan)
    {
        var status = plan.Status switch
        {
            CampaignContextPlanStatus.Ready => LanguageRuntime.GetString("Campaigns.ContextPlan.Ready"),
            CampaignContextPlanStatus.HistoryTrimmed => LanguageRuntime.GetString("Campaigns.ContextPlan.Trimmed"),
            CampaignContextPlanStatus.BlockedMandatoryContextTooLarge =>
                LanguageRuntime.GetString("Campaigns.ContextPlan.Blocked"),
            _ => plan.Status.ToString()
        };
        if (plan.Status == CampaignContextPlanStatus.BlockedMandatoryContextTooLarge
            && !string.IsNullOrWhiteSpace(plan.BlockingReason))
        {
            status = LanguageRuntime.Format(
                "Campaigns.ContextPlan.BlockedWithReasonFormat",
                status,
                ContextBlockReason(plan));
        }

        return plan.Estimate.IsExact
            ? status
            : LanguageRuntime.Format("Campaigns.ContextPlan.HeuristicFormat", status);
    }

    private static string ContextBlockReason(CampaignContextPlan plan) =>
        LanguageRuntime.BackendMessage(
            plan.BlockingReason,
            "Campaigns.ContextPreview.DefaultBlock");

    private static string ContextSectionTitle(
        CampaignContextSectionEstimate section) =>
        LanguageRuntime.GetString(section.Id switch
        {
            "player.global" => "Campaigns.ContextSection.PlayerGlobal",
            "player.protocol" => "Campaigns.ContextSection.PlayerProtocol",
            "player.world" => "Campaigns.ContextSection.PlayerWorld",
            "player.identity" => "Campaigns.ContextSection.PlayerIdentity",
            "player.character-card" => "Campaigns.ContextSection.PlayerCharacterCard",
            "player.initial-memory" => "Campaigns.ContextSection.PlayerInitialMemory",
            "player.history-header" => "Campaigns.ContextSection.PlayerHistoryHeader",
            "player.public-memory" => "Campaigns.ContextSection.PlayerPublicMemory",
            "player.history" => "Campaigns.ContextSection.PlayerHistory",
            "player.latest-gm" => "Campaigns.ContextSection.PlayerLatestGm",
            "player.pending-intents" => "Campaigns.ContextSection.PlayerPendingIntents",
            "player.current-task" => "Campaigns.ContextSection.PlayerCurrentTask",
            "gm.global" => "Campaigns.ContextSection.GmGlobal",
            "gm.protocol" => "Campaigns.ContextSection.GmProtocol",
            "gm.world" => "Campaigns.ContextSection.GmWorld",
            "gm.opening" => "Campaigns.ContextSection.GmOpening",
            "gm.roster" => "Campaigns.ContextSection.GmRoster",
            "gm.authority" => "Campaigns.ContextSection.GmAuthority",
            "gm.history-header" => "Campaigns.ContextSection.GmHistoryHeader",
            "gm.memory" => "Campaigns.ContextSection.GmMemory",
            "gm.history" => "Campaigns.ContextSection.GmHistory",
            "gm.current-intents" => "Campaigns.ContextSection.GmCurrentIntents",
            "gm.current-task" => "Campaigns.ContextSection.GmCurrentTask",
            _ => "Campaigns.ContextSection.Unknown"
        });

    private static string ContextSectionStateText(
        CampaignContextSectionEstimate section) =>
        section.Kind == ContextSegmentKind.Memory
        && !section.WasIncluded
        && !section.WasTruncated
        && section.EstimatedTokens == 0
            ? LanguageRuntime.GetString("Campaigns.ContextSection.Disabled")
            : section.WasIncluded
            ? section.WasTruncated
                ? LanguageRuntime.GetString("Campaigns.ContextSection.IncludedTrimmed")
                : LanguageRuntime.GetString("Campaigns.ContextSection.Included")
            : section.IsMandatory
                ? LanguageRuntime.GetString("Campaigns.ContextSection.MandatoryOverLimit")
                : section.WasTruncated
                    ? LanguageRuntime.GetString("Campaigns.ContextSection.Omitted")
                    : LanguageRuntime.GetString("Campaigns.ContextSection.NotIncluded");

}
