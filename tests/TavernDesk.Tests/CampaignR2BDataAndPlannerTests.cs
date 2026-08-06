using TavernDesk.Core.Models;
using TavernDesk.Infrastructure;
using TavernDesk.Infrastructure.Campaigns;
using TavernDesk.Infrastructure.Context;

namespace TavernDesk.Tests;

public sealed class CampaignR2BDataAndPlannerTests
{
    [Fact]
    public async Task CampaignContextSettingsPersistAndClampAtRepositoryBoundary()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var scenario = CreateScenario();
        await services.CampaignScenarios.UpsertAsync(scenario);
        var campaign = CreateCampaign(scenario.Id);
        campaign.ContextTokenBudget = 1;
        campaign.MemoryUpdateIntervalRounds = 100;
        campaign.MemoryUpdatePendingTokenThreshold = 0;
        campaign.MemoryEnabled = false;

        await services.Campaigns.SaveDraftAsync(campaign, CreateParticipants(campaign.Id));

        var clamped = await services.Campaigns.GetAsync(campaign.Id);
        Assert.NotNull(clamped);
        Assert.Equal(8_000, clamped.Campaign.ContextTokenBudget);
        Assert.Equal(50, clamped.Campaign.MemoryUpdateIntervalRounds);
        Assert.Equal(1_000, clamped.Campaign.MemoryUpdatePendingTokenThreshold);
        Assert.False(clamped.Campaign.MemoryEnabled);

        clamped.Campaign.ContextTokenBudget = 24_000;
        clamped.Campaign.MemoryUpdateIntervalRounds = 5;
        clamped.Campaign.MemoryUpdatePendingTokenThreshold = 6_000;
        clamped.Campaign.MemoryEnabled = true;
        await services.Campaigns.SaveDraftAsync(clamped.Campaign, clamped.Participants);

        var stored = await services.Campaigns.GetAsync(campaign.Id);
        Assert.NotNull(stored);
        Assert.Equal(24_000, stored.Campaign.ContextTokenBudget);
        Assert.Equal(5, stored.Campaign.MemoryUpdateIntervalRounds);
        Assert.Equal(6_000, stored.Campaign.MemoryUpdatePendingTokenThreshold);
        Assert.True(stored.Campaign.MemoryEnabled);
    }

    [Fact]
    public async Task SchemaV16AndV17MigrationsAddCampaignContextDefaults()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var scenario = CreateScenario();
        await services.CampaignScenarios.UpsertAsync(scenario);

        await using (var connection = services.Database.CreateConnection())
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                ALTER TABLE campaigns DROP COLUMN context_token_budget;
                ALTER TABLE campaigns DROP COLUMN memory_update_interval_rounds;
                ALTER TABLE campaigns DROP COLUMN memory_update_pending_token_threshold;
                ALTER TABLE campaigns DROP COLUMN memory_enabled;
                DELETE FROM schema_info WHERE version >= 16;
                """;
            await command.ExecuteNonQueryAsync();
        }

        await services.Database.InitializeAsync();
        var campaign = CreateCampaign(scenario.Id);
        await services.Campaigns.SaveDraftAsync(campaign, CreateParticipants(campaign.Id));
        var migrated = await services.Campaigns.GetAsync(campaign.Id);

        Assert.NotNull(migrated);
        Assert.Equal(15_000, migrated.Campaign.ContextTokenBudget);
        Assert.Equal(3, migrated.Campaign.MemoryUpdateIntervalRounds);
        Assert.Equal(4_000, migrated.Campaign.MemoryUpdatePendingTokenThreshold);
        Assert.True(migrated.Campaign.MemoryEnabled);
    }

    [Fact]
    public async Task ActiveCampaignMemoryTogglePersistsWithStateVersionGuard()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var scenario = CreateScenario();
        await services.CampaignScenarios.UpsertAsync(scenario);
        var campaign = CreateCampaign(scenario.Id);
        campaign.GmProviderId = "test-provider";
        var participants = CreateParticipants(campaign.Id);
        foreach (var participant in participants)
        {
            participant.ProviderId = "test-provider";
        }
        await services.Campaigns.SaveDraftAsync(campaign, participants);
        var started = await services.Campaigns.StartAsync(campaign.Id);

        await services.Campaigns.UpdateMemoryEnabledAsync(
            campaign.Id,
            started.Campaign.StateVersion,
            enabled: false);

        var disabled = await services.Campaigns.GetAsync(campaign.Id);
        Assert.NotNull(disabled);
        Assert.False(disabled.Campaign.MemoryEnabled);
        Assert.Equal(started.Campaign.StateVersion + 1, disabled.Campaign.StateVersion);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            services.Campaigns.UpdateMemoryEnabledAsync(
                campaign.Id,
                started.Campaign.StateVersion,
                enabled: true));
    }

    [Fact]
    public async Task PlannerUsesCampaignDefaultAndSmallerModelLimit()
    {
        var campaign = CreateCampaign();
        var participant = CreateParticipant(campaign.Id);
        var aggregate = new CampaignAggregate(campaign, [participant], []);
        var planner = new CampaignContextPlanner(new HeuristicTokenEstimator());

        var defaultPlan = await planner.BuildPlayerPlanAsync(aggregate, participant, null);
        Assert.Equal(15_000, defaultPlan.Estimate.ContextLimit);

        participant.ContextLimit = 6_000;
        var smallerPlan = await planner.BuildPlayerPlanAsync(aggregate, participant, null);
        Assert.Equal(6_000, smallerPlan.Estimate.ContextLimit);
    }

    [Fact]
    public async Task PlannerRetainsLatestGmAndTrimsOnlyOlderHistory()
    {
        var campaign = CreateCampaign();
        campaign.ContextTokenBudget = 8_000;
        var participant = CreateParticipant(campaign.Id);
        var events = new List<CampaignEvent>
        {
            Event(campaign, 1, CampaignEventKind.GmOpening, "opening")
        };
        for (var sequence = 2; sequence < 20; sequence++)
        {
            events.Add(Event(
                campaign,
                sequence,
                CampaignEventKind.GmResolution,
                new string('o', 1_800)));
        }

        events.Add(Event(
            campaign,
            20,
            CampaignEventKind.GmResolution,
            "LATEST AUTHORITATIVE SCENE"));
        events.Add(Event(
            campaign,
            21,
            CampaignEventKind.PlayerIntent,
            "current action",
            participant.Id));

        var aggregate = new CampaignAggregate(campaign, [participant], events);
        var planner = new CampaignContextPlanner(new HeuristicTokenEstimator());
        var plan = await planner.BuildPlayerPlanAsync(aggregate, participant, null);

        Assert.NotEqual(CampaignContextPlanStatus.BlockedMandatoryContextTooLarge, plan.Status);
        Assert.Contains(
            plan.Messages,
            message => message.Content.Contains("LATEST AUTHORITATIVE SCENE", StringComparison.Ordinal));
        Assert.Contains(
            plan.Sections,
            section => section.Id == "player.history" && section.WasTruncated);
        Assert.True(plan.Estimate.TotalTokens <= plan.Estimate.ContextLimit);
    }

    [Fact]
    public async Task PlannerBlocksWhenMandatoryCharacterCardCannotFit()
    {
        var campaign = CreateCampaign();
        campaign.ContextTokenBudget = 8_000;
        var participant = CreateParticipant(campaign.Id);
        participant.CharacterSnapshotJson = new string('x', 80_000);
        var aggregate = new CampaignAggregate(campaign, [participant], []);
        var planner = new CampaignContextPlanner(new HeuristicTokenEstimator());

        var plan = await planner.BuildPlayerPlanAsync(aggregate, participant, null);

        Assert.Equal(
            CampaignContextPlanStatus.BlockedMandatoryContextTooLarge,
            plan.Status);
        Assert.Contains("角色卡", plan.BlockingReason);
        Assert.Contains(
            plan.Sections,
            section => section.Id == "player.character-card"
                       && section.IsMandatory
                       && section.WasIncluded);
    }

    [Fact]
    public async Task PlannerDoesNotExposeGmOnlyEventsToPlayer()
    {
        var campaign = CreateCampaign();
        var participant = CreateParticipant(campaign.Id);
        var secret = Event(
            campaign,
            2,
            CampaignEventKind.StateDelta,
            "SECRET GM STATE",
            actorId: "gm:ai",
            visibility: CampaignVisibility.GmOnly);
        var aggregate = new CampaignAggregate(campaign, [participant], [secret]);
        var planner = new CampaignContextPlanner(new HeuristicTokenEstimator());

        var plan = await planner.BuildPlayerPlanAsync(aggregate, participant, null);

        Assert.DoesNotContain(
            plan.Messages,
            message => message.Content.Contains("SECRET GM STATE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PlannerCapsElasticMemoryToDynamicBudget()
    {
        var campaign = CreateCampaign();
        var participant = CreateParticipant(campaign.Id);
        var aggregate = new CampaignAggregate(campaign, [participant], []);
        var memory = new CampaignMemoryBank
        {
            CampaignId = campaign.Id,
            Scope = CampaignMemoryScope.Public,
            TargetTokens = 50_000,
            Body = new string('m', 80_000),
            SourceThroughEventSequence = 4
        };
        var planner = new CampaignContextPlanner(new HeuristicTokenEstimator());

        var plan = await planner.BuildPlayerPlanAsync(aggregate, participant, memory);
        var memorySection = Assert.Single(
            plan.Sections,
            section => section.Id == "player.public-memory");

        Assert.True(memorySection.WasIncluded);
        Assert.InRange(memorySection.EstimatedTokens, 1, 3_000);
    }

    [Fact]
    public async Task PlannerOmitsLongTermMemoryWhenDisabledButKeepsRecentHistory()
    {
        var campaign = CreateCampaign();
        campaign.MemoryEnabled = false;
        var participant = CreateParticipant(campaign.Id);
        var events = new[]
        {
            Event(campaign, 1, CampaignEventKind.GmResolution, "RECENT RAW GM SCENE")
        };
        var aggregate = new CampaignAggregate(campaign, [participant], events);
        var memory = new CampaignMemoryBank
        {
            CampaignId = campaign.Id,
            Scope = CampaignMemoryScope.Public,
            Body = "SECRET LONG TERM MEMORY",
            SourceThroughEventSequence = 1
        };
        var planner = new CampaignContextPlanner(new HeuristicTokenEstimator());

        var plan = await planner.BuildPlayerPlanAsync(
            aggregate,
            participant,
            memory,
            includeLongTermMemory: false);

        var memorySection = Assert.Single(
            plan.Sections,
            section => section.Id == "player.public-memory");
        Assert.False(memorySection.WasIncluded);
        Assert.Equal(0, memorySection.EstimatedTokens);
        Assert.DoesNotContain(
            plan.Messages,
            message => message.Content.Contains("SECRET LONG TERM MEMORY", StringComparison.Ordinal));
        Assert.Contains(
            plan.Messages,
            message => message.Content.Contains("RECENT RAW GM SCENE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GmPlannerKeepsAllCurrentRoundActionsTogether()
    {
        var campaign = CreateCampaign();
        var first = CreateParticipant(campaign.Id, "participant-one");
        var second = CreateParticipant(campaign.Id, "participant-two");
        var events = new[]
        {
            Event(campaign, 1, CampaignEventKind.PlayerIntent, "first action", first.Id),
            Event(campaign, 2, CampaignEventKind.PlayerIntent, "second action", second.Id),
            Event(campaign, 3, CampaignEventKind.DiceRoll, "1d20 = 15", "user")
        };
        var aggregate = new CampaignAggregate(campaign, [first, second], events);
        var planner = new CampaignContextPlanner(new HeuristicTokenEstimator());

        var plan = await planner.BuildGmPlanAsync(aggregate, null, null);

        Assert.Contains(plan.Messages, message => message.Content.Contains("first action"));
        Assert.Contains(plan.Messages, message => message.Content.Contains("second action"));
        Assert.Contains(plan.Messages, message => message.Content.Contains("1d20 = 15"));
    }

    private static CampaignScenario CreateScenario() =>
        new()
        {
            Id = "scenario-r2b",
            Title = "R2-B scenario",
            WorldSetting = "A small town",
            PublicRules = "Declared actions are inputs; GM resolution is authoritative."
        };

    private static Campaign CreateCampaign(string? storyId = null) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            StoryId = storyId ?? "scenario-r2b",
            Title = "R2-B campaign",
            WorldSetting = "A small town",
            Rules = "Declared actions are inputs; GM resolution is authoritative.",
            OpeningPrompt = "The story begins.",
            GmModelId = "test-gm",
            GmContextLimit = 32_768,
            GmMaxOutputTokens = 256
        };

    private static CampaignParticipant[] CreateParticipants(string campaignId) =>
    [
        new()
        {
            CampaignId = campaignId,
            Kind = CampaignParticipantKind.User,
            SortIndex = 0,
            DisplayName = "USER"
        },
        CreateParticipant(campaignId)
    ];

    private static CampaignParticipant CreateParticipant(
        string campaignId,
        string id = "participant-r2b") =>
        new()
        {
            Id = id,
            CampaignId = campaignId,
            Kind = CampaignParticipantKind.Ai,
            SortIndex = 1,
            DisplayName = "Player",
            CharacterSnapshotJson = "{\"name\":\"Player\",\"traits\":\"brave\"}",
            ModelId = "test-player",
            ContextLimit = 32_768,
            MaxOutputTokens = 256
        };

    private static CampaignEvent Event(
        Campaign campaign,
        long sequence,
        CampaignEventKind kind,
        string content,
        string actorId = "gm:ai",
        CampaignVisibility visibility = CampaignVisibility.Public) =>
        new()
        {
            Id = $"event-{sequence}",
            CampaignId = campaign.Id,
            SequenceNo = sequence,
            RoundNo = campaign.CurrentRound,
            Kind = kind,
            ActorId = actorId,
            Visibility = visibility,
            Content = content,
            GenerationStatus = CampaignGenerationStatus.Completed,
            IsLocked = true
        };
}
