using System.Text.Json.Nodes;
using TavernDesk.App.Services;
using TavernDesk.App.ViewModels;
using TavernDesk.Core.Models;
using TavernDesk.Infrastructure;
using TavernDesk.Infrastructure.Compatibility;

namespace TavernDesk.Tests;

public sealed class CampaignTests
{
    [Fact]
    public async Task ScenarioEditorLoadsStructuredFieldsAndPersistsOnlyTheScenarioCopy()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var scenario = CreateScenario();
        scenario.SourceCardJson = "{\"source_extension\":true}";
        scenario.SourceFileName = "original-card.png";
        scenario.LegacyExamplesArchive = "旧示例";
        await services.CampaignScenarios.UpsertAsync(scenario);

        var viewModel = new CampaignsViewModel(
            services.CampaignScenarios,
            services.CampaignScenarioCards,
            services.Campaigns,
            services.CampaignRunner,
            services.Characters,
            services.CampaignCharacterSnapshots,
            services.MemoryBanks,
            services.Providers,
            services.Models,
            services.ModelAssignments,
            services.Settings,
            new NoOpFileDialogService(),
            new CampaignInteractionService());
        await viewModel.LoadAsync();
        viewModel.SelectedScenario = viewModel.Scenarios.Single();

        viewModel.EditScenarioCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.IsScenarioEditor);

        Assert.Equal(scenario.Title, viewModel.ScenarioTitle);
        Assert.Equal(scenario.WorldSetting, viewModel.ScenarioWorldSetting);
        Assert.Equal(scenario.GmInstructions, viewModel.ScenarioGmInstructions);
        Assert.Equal(scenario.LegacyExamplesArchive, viewModel.ScenarioLegacyExamplesArchive);

        viewModel.ScenarioTitle = "编辑后的剧本标题";
        viewModel.ScenarioSummary = "编辑后的简介";
        viewModel.ScenarioWorldSetting = "编辑后的世界观";
        viewModel.ScenarioPublicRules = "编辑后的规则";
        viewModel.ScenarioGmInstructions = "编辑后的 GM 指令";
        viewModel.ScenarioOpeningSetup = "编辑后的开场设置";
        viewModel.ScenarioOpeningNarration = "编辑后的开场旁白";
        viewModel.ScenarioLegacyExamplesArchive = "编辑后的历史归档";
        viewModel.SaveScenarioCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.IsLibrary);

        var saved = await services.CampaignScenarios.GetAsync(scenario.Id);
        Assert.NotNull(saved);
        Assert.Equal("编辑后的剧本标题", saved.Title);
        Assert.Equal("编辑后的简介", saved.Summary);
        Assert.Equal("编辑后的世界观", saved.WorldSetting);
        Assert.Equal("编辑后的规则", saved.PublicRules);
        Assert.Equal("编辑后的 GM 指令", saved.GmInstructions);
        Assert.Equal("编辑后的开场设置", saved.OpeningSetup);
        Assert.Equal("编辑后的开场旁白", saved.OpeningNarration);
        Assert.Equal("编辑后的历史归档", saved.LegacyExamplesArchive);
        Assert.Equal(scenario.SourceCardJson, saved.SourceCardJson);
        Assert.Equal(scenario.SourceFileName, saved.SourceFileName);
    }

    [Fact]
    public async Task CampaignContextDeleteRemovesOnlyTheConfirmedRun()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var scenario = CreateScenario();
        await services.CampaignScenarios.UpsertAsync(scenario);
        var deletedCampaign = CreateCampaign(scenario.Id);
        var retainedCampaign = CreateCampaign(scenario.Id);
        retainedCampaign.Title = "保留的跑团";
        await services.Campaigns.SaveDraftAsync(
            deletedCampaign,
            CreateParticipants(deletedCampaign.Id));
        await services.Campaigns.SaveDraftAsync(
            retainedCampaign,
            CreateParticipants(retainedCampaign.Id, "ai-retained"));
        await services.Campaigns.StartAsync(deletedCampaign.Id);

        var interaction = new CampaignInteractionService(confirmDeletion: true);
        var viewModel = new CampaignsViewModel(
            services.CampaignScenarios,
            services.CampaignScenarioCards,
            services.Campaigns,
            services.CampaignRunner,
            services.Characters,
            services.CampaignCharacterSnapshots,
            services.MemoryBanks,
            services.Providers,
            services.Models,
            services.ModelAssignments,
            services.Settings,
            new NoOpFileDialogService(),
            interaction);
        await viewModel.LoadAsync();
        var item = viewModel.Campaigns.Single(campaign =>
            campaign.Id == deletedCampaign.Id);

        viewModel.DeleteCampaignCommand.Execute(item);
        await WaitUntilAsync(() => viewModel.Campaigns.Count == 1);

        Assert.Equal(deletedCampaign.Title, interaction.ConfirmedTitle);
        Assert.Equal(1, interaction.ConfirmedEventCount);
        Assert.Null(await services.Campaigns.GetAsync(deletedCampaign.Id));
        Assert.NotNull(await services.Campaigns.GetAsync(retainedCampaign.Id));
        Assert.NotNull(await services.CampaignScenarios.GetAsync(scenario.Id));
        Assert.Equal(retainedCampaign.Id, viewModel.Campaigns.Single().Id);
    }

    [Fact]
    public async Task StartingCampaignFreezesDraftAndCloneCreatesIndependentRun()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var scenario = CreateScenario();
        await services.CampaignScenarios.UpsertAsync(scenario);
        var campaign = CreateCampaign(scenario.Id);
        campaign.MemoryEnabled = false;
        var participants = CreateParticipants(campaign.Id);
        await services.Campaigns.SaveDraftAsync(campaign, participants);

        var started = await services.Campaigns.StartAsync(campaign.Id);

        Assert.Equal(CampaignStatus.Active, started.Campaign.Status);
        Assert.Equal(CampaignPhase.Opening, started.Campaign.Phase);
        Assert.True(started.Campaign.IsFrozen);
        Assert.Equal(1, started.Campaign.FrozenSequenceNo);
        Assert.Equal(2, started.Participants.Count);
        Assert.Single(started.Events);
        Assert.Contains("已冻结", started.Events[0].Content);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => services.Campaigns.SaveDraftAsync(
                started.Campaign,
                started.Participants));

        var clone = await services.Campaigns.CloneAsDraftAsync(campaign.Id);
        Assert.NotEqual(campaign.Id, clone.Campaign.Id);
        Assert.Equal(campaign.Id, clone.Campaign.ParentCampaignId);
        Assert.Equal(campaign.StoryId, clone.Campaign.StoryId);
        Assert.Equal(CampaignStatus.Draft, clone.Campaign.Status);
        Assert.False(clone.Campaign.MemoryEnabled);
        Assert.Empty(clone.Events);
        Assert.All(
            clone.Participants,
            participant => Assert.Equal(clone.Campaign.Id, participant.CampaignId));
        Assert.Equal(
            started.Participants.Single(item => item.Kind == CampaignParticipantKind.Ai)
                .CharacterSnapshotJson,
            clone.Participants.Single(item => item.Kind == CampaignParticipantKind.Ai)
                .CharacterSnapshotJson);

        clone.Campaign.Title = "独立的新一局";
        await services.Campaigns.SaveDraftAsync(clone.Campaign, clone.Participants);
        var original = await services.Campaigns.GetAsync(campaign.Id);
        Assert.Equal("火影跑团", original!.Campaign.Title);
    }

    [Fact]
    public async Task ActiveAiRouteChangePreservesFrozenCharacterSnapshotAndLeavesAuditEvent()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var scenario = CreateScenario();
        await services.CampaignScenarios.UpsertAsync(scenario);
        var campaign = CreateCampaign(scenario.Id);
        var participants = CreateParticipants(campaign.Id);
        await services.Campaigns.SaveDraftAsync(campaign, participants);
        var started = await services.Campaigns.StartAsync(campaign.Id);
        var ai = started.Participants.Single(item =>
            item.Kind == CampaignParticipantKind.Ai);
        var snapshot = ai.CharacterSnapshotJson;

        await services.Campaigns.UpdateParticipantRouteAsync(
            campaign.Id,
            ai.Id,
            new CampaignModelRoute(
                "openrouter",
                "qwen/qwen3",
                65536,
                2048,
                0.6,
                0.95));

        var reloaded = await services.Campaigns.GetAsync(campaign.Id);
        var changed = reloaded!.Participants.Single(item => item.Id == ai.Id);
        Assert.Equal("qwen/qwen3", changed.ModelId);
        Assert.Equal(snapshot, changed.CharacterSnapshotJson);
        Assert.Contains(
            reloaded.Events,
            item => item.Kind == CampaignEventKind.System
                    && item.Content.Contains("qwen/qwen3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UserOnlyCampaignCanStartAndAdvanceWithoutAiPlayers()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var scenario = CreateScenario();
        await services.CampaignScenarios.UpsertAsync(scenario);
        var campaign = CreateCampaign(scenario.Id);
        campaign.GmKind = CampaignGmKind.User;
        campaign.UserAlsoPlayer = true;
        var user = new CampaignParticipant
        {
            CampaignId = campaign.Id,
            Kind = CampaignParticipantKind.User,
            SortIndex = 0,
            DisplayName = "旅行者"
        };
        await services.Campaigns.SaveDraftAsync(campaign, [user]);

        var started = await services.CampaignRunner.StartAsync(campaign.Id);

        Assert.Single(started.Participants);
        Assert.Equal(
            CampaignParticipantKind.User,
            started.Participants[0].Kind);
        Assert.Equal(CampaignPhase.AwaitingActions, started.Campaign.Phase);
        Assert.Empty(
            await services.CampaignRunner.GenerateAiActionsAsync(campaign.Id));

        await services.CampaignRunner.SubmitUserActionAsync(
            campaign.Id,
            "先检查周围环境。");
        var awaitingGm = await services.Campaigns.GetAsync(campaign.Id);
        Assert.Equal(
            CampaignPhase.ReadyForResolution,
            awaitingGm!.Campaign.Phase);

        await services.CampaignRunner.SubmitUserGmResolutionAsync(
            campaign.Id,
            "你没有发现即时危险，远处传来脚步声。");
        var nextRound = await services.Campaigns.GetAsync(campaign.Id);
        Assert.Equal(CampaignPhase.AwaitingActions, nextRound!.Campaign.Phase);
        Assert.Equal(2, nextRound.Campaign.CurrentRound);
    }

    [Fact]
    public void CampaignSnapshotSeparatesGreetingsAndOptionalOriginalWorldKnowledge()
    {
        var character = new Character
        {
            Name = "雪乃",
            Description = "角色描述",
            Personality = "冷静",
            Scenario = "原世界学校场景",
            FirstMessage = "不应进入跑团的首次发言",
            RawCardJson = """
                {
                  "spec": "chara_card_v2",
                  "spec_version": "2.0",
                  "data": {
                    "name": "雪乃",
                    "description": "角色描述",
                    "personality": "冷静",
                    "scenario": "原世界学校场景",
                    "first_mes": "不应进入跑团的首次发言",
                    "alternate_greetings": ["也不应进入"],
                    "creator_notes": "仅导入说明",
                    "system_prompt": "保持雪乃的说话方式",
                    "post_history_instructions": "不替 USER 作决定",
                    "mes_example": "雪乃：示例对白",
                    "character_book": {
                      "entries": [
                        { "keys": ["学校"], "content": "原世界知识" }
                      ]
                    }
                  }
                }
                """
        };
        var adapter = new CampaignCharacterSnapshotAdapter();

        var isolated = adapter.Create(
            character,
            "普通聊天记忆",
            includeMemory: false,
            includeOriginalWorldKnowledge: false);
        Assert.DoesNotContain("不应进入跑团", isolated.CharacterSnapshotJson);
        Assert.DoesNotContain("也不应进入", isolated.CharacterSnapshotJson);
        Assert.DoesNotContain("仅导入说明", isolated.CharacterSnapshotJson);
        Assert.Contains("保持雪乃", isolated.CharacterSnapshotJson);
        Assert.Contains("示例对白", isolated.CharacterSnapshotJson);
        Assert.Equal(string.Empty, isolated.MemorySnapshot);
        Assert.DoesNotContain("原世界知识", isolated.OriginalWorldKnowledgeSnapshot);

        var imported = adapter.Create(
            character,
            "普通聊天记忆",
            includeMemory: true,
            includeOriginalWorldKnowledge: true);
        Assert.Equal("普通聊天记忆", imported.MemorySnapshot);
        Assert.Contains("原世界学校场景", imported.OriginalWorldKnowledgeSnapshot);
        Assert.Contains("原世界知识", imported.OriginalWorldKnowledgeSnapshot);
        Assert.DoesNotContain("不应进入跑团", imported.CharacterSnapshotJson);
    }

    [Fact]
    public async Task EventOperationIdIsIdempotentAndTerminalCacheCannotBeRewritten()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var scenario = CreateScenario();
        await services.CampaignScenarios.UpsertAsync(scenario);
        var campaign = CreateCampaign(scenario.Id);
        await services.Campaigns.SaveDraftAsync(
            campaign,
            CreateParticipants(campaign.Id));
        await services.Campaigns.StartAsync(campaign.Id);
        var queued = new CampaignEvent
        {
            CampaignId = campaign.Id,
            RoundNo = 1,
            Kind = CampaignEventKind.PlayerIntent,
            ActorId = "ai-1",
            Visibility = CampaignVisibility.Public,
            OperationId = "operation-1",
            GenerationStatus = CampaignGenerationStatus.Queued
        };
        var first = await services.Campaigns.AppendEventAsync(queued);
        var duplicate = await services.Campaigns.AppendEventAsync(new CampaignEvent
        {
            CampaignId = campaign.Id,
            RoundNo = 1,
            Kind = CampaignEventKind.PlayerIntent,
            ActorId = "ai-1",
            Visibility = CampaignVisibility.Public,
            OperationId = "operation-1",
            GenerationStatus = CampaignGenerationStatus.Queued
        });
        Assert.Equal(first.Id, duplicate.Id);

        first.Content = "完整行动";
        first.GenerationStatus = CampaignGenerationStatus.Completed;
        first.EndReason = CampaignEndReason.Normal;
        first.IsLocked = true;
        await services.Campaigns.UpdateEventAsync(first);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => services.Campaigns.UpdateEventAsync(first));
    }

    [Fact]
    public async Task RestartRecoversStaleGenerationsWithoutChangingTerminalEvents()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var scenario = CreateScenario();
        await services.CampaignScenarios.UpsertAsync(scenario);
        var campaign = CreateCampaign(scenario.Id);
        await services.Campaigns.SaveDraftAsync(
            campaign,
            CreateParticipants(campaign.Id));
        await services.Campaigns.StartAsync(campaign.Id);

        await services.Campaigns.AppendEventAsync(new CampaignEvent
        {
            CampaignId = campaign.Id,
            RoundNo = 1,
            Kind = CampaignEventKind.PlayerIntent,
            ActorId = "ai-1",
            Visibility = CampaignVisibility.Public,
            OperationId = "queued-before-exit",
            GenerationStatus = CampaignGenerationStatus.Queued
        });
        var streaming = await services.Campaigns.AppendEventAsync(new CampaignEvent
        {
            CampaignId = campaign.Id,
            RoundNo = 1,
            Kind = CampaignEventKind.PlayerIntent,
            ActorId = "ai-1",
            Visibility = CampaignVisibility.Public,
            OperationId = "streaming-before-exit",
            GenerationStatus = CampaignGenerationStatus.Queued
        });
        streaming.Content = "已持久化的部分内容";
        streaming.GenerationStatus = CampaignGenerationStatus.Streaming;
        await services.Campaigns.UpdateEventAsync(streaming);
        var completed = await services.Campaigns.AppendEventAsync(new CampaignEvent
        {
            CampaignId = campaign.Id,
            RoundNo = 1,
            Kind = CampaignEventKind.PlayerIntent,
            ActorId = "ai-1",
            Visibility = CampaignVisibility.Public,
            Content = "已经完成的内容",
            OperationId = "completed-before-exit",
            GenerationStatus = CampaignGenerationStatus.Completed,
            EndReason = CampaignEndReason.Normal,
            IsLocked = true
        });

        var restarted = new InfrastructureServices(workspace.Root);
        await restarted.InitializeAsync();
        var recovered = await restarted.Campaigns.GetAsync(campaign.Id);

        Assert.NotNull(recovered);
        var queuedAfterRestart = recovered.Events.Single(
            item => item.OperationId == "queued-before-exit");
        Assert.Equal(
            CampaignGenerationStatus.Interrupted,
            queuedAfterRestart.GenerationStatus);
        Assert.Equal(CampaignEndReason.ProcessExited, queuedAfterRestart.EndReason);
        Assert.False(queuedAfterRestart.IsLocked);

        var streamingAfterRestart = recovered.Events.Single(
            item => item.OperationId == "streaming-before-exit");
        Assert.Equal(
            CampaignGenerationStatus.Interrupted,
            streamingAfterRestart.GenerationStatus);
        Assert.Equal(CampaignEndReason.ProcessExited, streamingAfterRestart.EndReason);
        Assert.Equal("已持久化的部分内容", streamingAfterRestart.Content);
        Assert.False(streamingAfterRestart.IsLocked);

        var completedAfterRestart = recovered.Events.Single(
            item => item.OperationId == completed.OperationId);
        Assert.Equal(
            CampaignGenerationStatus.Completed,
            completedAfterRestart.GenerationStatus);
        Assert.Equal(CampaignEndReason.Normal, completedAfterRestart.EndReason);
        Assert.Equal("已经完成的内容", completedAfterRestart.Content);
        Assert.True(completedAfterRestart.IsLocked);

        var recoveredAt = streamingAfterRestart.UpdatedAt;
        await restarted.InitializeAsync();
        var recoveredAgain = await restarted.Campaigns.GetAsync(campaign.Id);
        var streamingAfterSecondInitialization = recoveredAgain!.Events.Single(
            item => item.OperationId == "streaming-before-exit");
        Assert.Equal(
            CampaignGenerationStatus.Interrupted,
            streamingAfterSecondInitialization.GenerationStatus);
        Assert.Equal(
            CampaignEndReason.ProcessExited,
            streamingAfterSecondInitialization.EndReason);
        Assert.Equal(recoveredAt, streamingAfterSecondInitialization.UpdatedAt);
    }

    private static CampaignScenario CreateScenario() =>
        new()
        {
            Title = "火影忍者：禁术卷轴",
            Summary = "任务",
            WorldSetting = "忍界",
            PublicRules = "玩家声明意图，GM 裁决。",
            GmInstructions = "保持世界因果。",
            OpeningSetup = "木叶出现古老卷轴。",
            OpeningNarration = "众人来到火影办公室。"
        };

    private static Campaign CreateCampaign(string scenarioId) =>
        new()
        {
            StoryId = scenarioId,
            Title = "火影跑团",
            WorldSetting = "忍界",
            Rules = "玩家声明意图，GM 裁决。",
            OpeningPrompt = "木叶出现古老卷轴。",
            GmKind = CampaignGmKind.Ai,
            UserAlsoPlayer = true,
            GmProviderId = "openrouter",
            GmModelId = "deepseek/deepseek-v4-flash-0731",
            GmContextLimit = 65536,
            GmMaxOutputTokens = 2048
        };

    private static CampaignParticipant[] CreateParticipants(
        string campaignId,
        string aiId = "ai-1") =>
    [
        new()
        {
            CampaignId = campaignId,
            Kind = CampaignParticipantKind.User,
            SortIndex = 0,
            DisplayName = "USER"
        },
        new()
        {
            Id = aiId,
            CampaignId = campaignId,
            Kind = CampaignParticipantKind.Ai,
            SortIndex = 1,
            SourceCharacterId = null,
            DisplayName = "雪乃",
            CharacterSnapshotJson = JsonNode.Parse(
                """{"schema":"taverndesk.campaign-character.v1","identity":{"name":"雪乃"}}""")!
                .ToJsonString(),
            ProviderId = "openrouter",
            ModelId = "deepseek/deepseek-v4-flash-0731",
            ContextLimit = 65536,
            MaxOutputTokens = 1024
        }
    ];

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.True(condition(), "等待 ViewModel 状态变化超时。");
    }

    private sealed class CampaignInteractionService(
        bool confirmDeletion = false) : IUserInteractionService
    {
        public string? ConfirmedTitle { get; private set; }
        public int ConfirmedEventCount { get; private set; }

        public Task<string?> EditTextAsync(
            string title,
            string prompt,
            string initialText) =>
            Task.FromResult<string?>(null);

        public DeleteMessageDecision ConfirmMessageDeletion() =>
            DeleteMessageDecision.Cancel;

        public UnsavedChangesDecision ConfirmUnsavedCharacterChanges(
            string characterName) =>
            UnsavedChangesDecision.Cancel;

        public UnsavedChangesDecision ConfirmUnsavedProviderChanges(
            string providerName) =>
            UnsavedChangesDecision.Cancel;

        public bool ConfirmCharacterDeletion(
            string characterName,
            int conversationCount) => false;

        public bool ConfirmShelfDeletion(string shelfName) => false;

        public bool ConfirmPresetDeletion(string presetName) => false;

        public bool ConfirmProviderDeletion(string providerName) => false;

        public bool ConfirmCampaignDeletion(
            string campaignTitle,
            int eventCount)
        {
            ConfirmedTitle = campaignTitle;
            ConfirmedEventCount = eventCount;
            return confirmDeletion;
        }

        public bool ConfirmSecretClear(string providerName) => false;

        public Task<GroupChatDraft?> CreateGroupChatAsync(
            IReadOnlyList<Character> characters) =>
            Task.FromResult<GroupChatDraft?>(null);

        public void CopyText(string text)
        {
        }
    }
}
