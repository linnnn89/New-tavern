using System.Runtime.CompilerServices;
using System.Text.Json;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;
using TavernDesk.Infrastructure;
using TavernDesk.Infrastructure.Campaigns;

namespace TavernDesk.Tests;

public sealed class CampaignRunnerTests
{
    [Fact]
    public async Task CampaignRunsFromFrozenLobbyThroughAiGmResolutionWithoutPromptLeakage()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var scenario = new CampaignScenario
        {
            Title = "火影测试剧本",
            WorldSetting = "忍者世界",
            PublicRules = "行动结果由 GM 裁定",
            GmInstructions = "GM_PRIVATE_RULE",
            OpeningSetup = "卷轴出现",
            OpeningNarration = "乌云压住木叶，禁术卷轴在雨中显现。",
            LegacyExamplesArchive = "LEGACY_CHAT_ARCHIVE"
        };
        await services.CampaignScenarios.UpsertAsync(scenario);
        var campaign = new Campaign
        {
            StoryId = scenario.Id,
            Title = "第一局",
            WorldSetting = scenario.WorldSetting,
            Rules = scenario.PublicRules,
            OpeningPrompt = scenario.OpeningSetup,
            GmKind = CampaignGmKind.Ai,
            FlowPreset = CampaignFlowPreset.CollaborativeTable,
            GmProviderId = "openrouter",
            GmModelId = "gm-model"
        };
        var user = new CampaignParticipant
        {
            CampaignId = campaign.Id,
            Kind = CampaignParticipantKind.User,
            SortIndex = 0,
            DisplayName = "林楠",
            PersonaSnapshotJson =
                """{"name":"林楠","description":"USER_SNAPSHOT"}"""
        };
        var ai = new CampaignParticipant
        {
            CampaignId = campaign.Id,
            Kind = CampaignParticipantKind.Ai,
            SortIndex = 1,
            DisplayName = "雪乃",
            CharacterSnapshotJson =
                """{"identity":{"name":"雪乃"},"behavior":{"system_prompt":"保持冷静"}}""",
            ProviderId = "openrouter",
            ModelId = "player-model"
        };
        await services.Campaigns.SaveDraftAsync(campaign, [user, ai]);
        var promptProfile = services.GlobalPrompts.Snapshot()
            .ToDictionary(item => item.Key, item => item.Value);
        promptProfile[GlobalPromptKey.CampaignPlayerSystem] =
            "CUSTOM_PLAYER_DUTY";
        promptProfile[GlobalPromptKey.CampaignGmSystem] =
            "CUSTOM_GM_DUTY";
        await services.GlobalPrompts.SaveAsync(promptProfile);
        var gateway = new RecordingCampaignGateway();
        var runner = new CampaignRunner(
            services.Campaigns,
            services.CampaignScenarios,
            gateway,
            services.GenerationCoordinator,
            services.GlobalPrompts);

        var started = await runner.StartAsync(campaign.Id);
        Assert.Equal(CampaignPhase.AwaitingActions, started.Campaign.Phase);
        Assert.Contains(
            started.Events,
            item => item.Kind == CampaignEventKind.GmOpening
                    && item.Content.Contains("禁术卷轴")
                    && item.Content.Contains("【下一轮评定参考】"));
        Assert.DoesNotContain(
            "【下一轮评定参考】",
            started.Campaign.WorldSummary,
            StringComparison.Ordinal);

        var userAction = await runner.SubmitUserActionAsync(
            campaign.Id,
            "我先检查卷轴周围的封印。");
        AssertAutomaticActionRoll(userAction);
        var aiActions = await runner.GenerateAiActionsAsync(campaign.Id);
        Assert.Single(aiActions);
        Assert.Equal(CampaignGenerationStatus.Completed, aiActions[0].GenerationStatus);
        AssertAutomaticActionRoll(aiActions[0]);
        var ready = await services.Campaigns.GetAsync(campaign.Id);
        Assert.Equal(CampaignPhase.ReadyForResolution, ready!.Campaign.Phase);

        var resolution = await runner.GenerateGmResolutionAsync(campaign.Id);
        Assert.Equal(CampaignGenerationStatus.Completed, resolution.GenerationStatus);
        Assert.Contains(
            "【下一轮评定参考】",
            resolution.Content,
            StringComparison.Ordinal);
        var nextRound = await services.Campaigns.GetAsync(campaign.Id);
        Assert.Equal(2, nextRound!.Campaign.CurrentRound);
        Assert.Equal(CampaignPhase.AwaitingActions, nextRound.Campaign.Phase);

        var allPromptText = string.Join(
            "\n",
            gateway.Requests.SelectMany(request =>
                request.Messages.Select(message => message.Content)));
        Assert.DoesNotContain("LOBBY_ONLY_FIRST_MESSAGE", allPromptText);
        Assert.DoesNotContain("LEGACY_CHAT_ARCHIVE", allPromptText);
        Assert.Contains(
            "CUSTOM_PLAYER_DUTY",
            gateway.Requests[0].Messages[0].Content);
        Assert.Contains(
            "CUSTOM_GM_DUTY",
            gateway.Requests[^1].Messages[0].Content);
        Assert.Contains("GM_PRIVATE_RULE", gateway.Requests[^1].Messages[0].Content);
        Assert.DoesNotContain("GM_PRIVATE_RULE", gateway.Requests[0].Messages[0].Content);
        Assert.Contains(
            "【TavernDesk 自动行动骰协议】",
            gateway.Requests[0].Messages[0].Content,
            StringComparison.Ordinal);
        Assert.Contains(
            "【TavernDesk 强制回合协议】",
            gateway.Requests[^1].Messages[0].Content,
            StringComparison.Ordinal);
        Assert.Contains(
            "【玩家席位与所有权】",
            gateway.Requests[^1].Messages[0].Content,
            StringComparison.Ordinal);
        Assert.Contains(
            "USER_SNAPSHOT",
            gateway.Requests[^1].Messages[0].Content,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"name\":\"雪乃\"",
            gateway.Requests[^1].Messages[0].Content,
            StringComparison.Ordinal);
        Assert.Contains(
            "不能替其生成新台词、心理、决定、反应或下一步行动",
            gateway.Requests[^1].Messages[0].Content,
            StringComparison.Ordinal);
        var playerRequest = gateway.Requests[0];
        var gmRequest = gateway.Requests[^1];
        Assert.Equal(
            $"campaign:{campaign.Id}:player:{ai.Id}",
            playerRequest.SessionId);
        Assert.Equal($"campaign:{campaign.Id}:gm", gmRequest.SessionId);
        Assert.StartsWith(
            "【已裁定共同历史】",
            playerRequest.Messages[^1].Content,
            StringComparison.Ordinal);
        Assert.Contains(
            "【最新 GM 场景与裁定｜当前行动依据】",
            playerRequest.Messages[^1].Content,
            StringComparison.Ordinal);
        Assert.Contains(
            "【本轮其他席位已提交内容｜结果等待 GM 裁定】",
            playerRequest.Messages[^1].Content,
            StringComparison.Ordinal);
        Assert.Contains(
            "以最新 GM 场景和裁定为权威起点",
            playerRequest.Messages[^1].Content,
            StringComparison.Ordinal);
        Assert.True(
            playerRequest.Messages[^1].Content.IndexOf(
                "【最新 GM 场景与裁定｜当前行动依据】",
                StringComparison.Ordinal)
            < playerRequest.Messages[^1].Content.IndexOf(
                "【本轮其他席位已提交内容｜结果等待 GM 裁定】",
                StringComparison.Ordinal));
        Assert.StartsWith(
            "【已裁定历史】",
            gmRequest.Messages[^1].Content,
            StringComparison.Ordinal);
        Assert.Contains(
            "【本轮待裁定行动】",
            gmRequest.Messages[^1].Content,
            StringComparison.Ordinal);
        Assert.Contains(
            "【本轮 GM 输出任务】",
            gmRequest.Messages[^1].Content,
            StringComparison.Ordinal);
        Assert.Contains(
            "禁止复制、转述、概括或重新表演任何 PlayerIntent",
            gmRequest.Messages[^1].Content,
            StringComparison.Ordinal);
        Assert.Contains(
            "玩家已经说出的台词和公开表达可以视为角色已提交的公开行为",
            gmRequest.Messages[0].Content,
            StringComparison.Ordinal);
        Assert.Contains(
            "行动是否成功、观察是否正确，以及对 NPC、环境和世界造成的影响仍待本次裁定",
            gmRequest.Messages[0].Content,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "刚刚发生完毕的输入",
            gmRequest.Messages[0].Content,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "从上述行动全部结束后的时间点继续",
            gmRequest.Messages[^1].Content,
            StringComparison.Ordinal);
        var gmPromptSections = gmRequest.Messages[^1].Content.Split(
            "【本轮待裁定行动】",
            StringSplitOptions.None);
        Assert.Equal(2, gmPromptSections.Length);
        Assert.DoesNotContain(
            "我先检查卷轴周围的封印。",
            gmPromptSections[0],
            StringComparison.Ordinal);
        Assert.Contains(
            "我先检查卷轴周围的封印。",
            gmPromptSections[1],
            StringComparison.Ordinal);
        Assert.DoesNotContain("现在是第", playerRequest.Messages[^1].Content);
        Assert.DoesNotContain("请提交", playerRequest.Messages[^1].Content);
        Assert.DoesNotContain("现在是第", gmRequest.Messages[^1].Content);
        Assert.DoesNotContain("请裁定", gmRequest.Messages[^1].Content);
        Assert.Equal(
            2,
            CountOccurrences(
                gmRequest.Messages[^1].Content,
                "【随行动评定骰】"));
        Assert.DoesNotContain(
            "【下一轮评定参考】",
            nextRound.Campaign.WorldSummary,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlayerPromptKeepsSpeakerOwnershipAndMarksCurrentRoundIntentsPending()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var scenario = new CampaignScenario
        {
            Title = "发言归属测试",
            WorldSetting = "多人共同在研究社活动室中。",
            PublicRules = "每名玩家只控制自己的角色。",
            OpeningSetup = "所有人已经到场。",
            OpeningNarration = "研究社活动室内，所有人等待下一步行动。"
        };
        await services.CampaignScenarios.UpsertAsync(scenario);
        var campaign = new Campaign
        {
            StoryId = scenario.Id,
            Title = scenario.Title,
            WorldSetting = scenario.WorldSetting,
            Rules = scenario.PublicRules,
            OpeningPrompt = scenario.OpeningSetup,
            GmKind = CampaignGmKind.User,
            FlowPreset = CampaignFlowPreset.CollaborativeTable
        };
        var user = new CampaignParticipant
        {
            CampaignId = campaign.Id,
            Kind = CampaignParticipantKind.User,
            SortIndex = 0,
            DisplayName = "林楠",
            PersonaSnapshotJson = """{"name":"林楠"}"""
        };
        var alter = CreateAiParticipant(
            campaign.Id,
            1,
            "黑贞德",
            "alter-model");
        var orihime = CreateAiParticipant(
            campaign.Id,
            2,
            "井上织姬",
            "orihime-model");
        await services.Campaigns.SaveDraftAsync(campaign, [user, alter, orihime]);
        var gateway = new RecordingCampaignGateway();
        var runner = new CampaignRunner(
            services.Campaigns,
            services.CampaignScenarios,
            gateway,
            services.GenerationCoordinator,
            services.GlobalPrompts);
        await runner.StartAsync(campaign.Id);
        await runner.SubmitUserActionAsync(
            campaign.Id,
            "要求莉雅丝测试我被转生后获得的能力。");
        await runner.GenerateAiActionAsync(campaign.Id, alter.Id);
        await runner.GenerateAiActionAsync(campaign.Id, orihime.Id);

        var request = gateway.Requests.Single(item =>
            item.ModelId == "orihime-model");
        var system = request.Messages[0].Content;
        var payload = request.Messages[1].Content;

        Assert.Contains(
            $"\"current_actor\":{{\"kind\":\"ai_player\",\"id\":\"{orihime.Id}\",\"name\":\"井上织姬\"}}",
            system,
            StringComparison.Ordinal);
        Assert.Contains(
            "当前输出作者只能是“井上织姬”",
            system,
            StringComparison.Ordinal);
        Assert.Contains(
            $"\"kind\":\"user_player\",\"id\":\"{user.Id}\",\"name\":\"林楠\",\"is_current_actor\":false",
            system,
            StringComparison.Ordinal);
        Assert.Contains(
            $"\"speaker\":{{\"kind\":\"user_player\",\"id\":\"{user.Id}\",\"name\":\"林楠\"}}",
            payload,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"resolution_status\":\"pending_gm_resolution\"",
            payload,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"content\":\"要求莉雅丝测试我被转生后获得的能力。",
            payload,
            StringComparison.Ordinal);
        Assert.Contains(
            $"\"speaker\":{{\"kind\":\"ai_player\",\"id\":\"{alter.Id}\",\"name\":\"黑贞德\"}}",
            payload,
            StringComparison.Ordinal);
        Assert.StartsWith(
            "【已裁定共同历史】",
            payload,
            StringComparison.Ordinal);
        Assert.Contains(
            "【最新 GM 场景与裁定｜当前行动依据】",
            payload,
            StringComparison.Ordinal);
        Assert.Contains(
            "【本轮其他席位已提交内容｜结果等待 GM 裁定】",
            payload,
            StringComparison.Ordinal);
        Assert.Contains(
            "只为 current_actor“井上织姬”",
            payload,
            StringComparison.Ordinal);
        Assert.Contains(
            "以最新 GM 场景和裁定为权威起点",
            payload,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "先回答 GM",
            payload,
            StringComparison.Ordinal);
        Assert.DoesNotContain("[R1 #", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlayerPromptKeepsResolvedHistoryChronologicalAndCurrentRoundIntentsSeparate()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var scenario = new CampaignScenario
        {
            Title = "事件生命周期测试",
            WorldSetting = "封闭活动室",
            PublicRules = "GM 裁定世界结果。",
            OpeningSetup = "所有席位已经到场。",
            OpeningNarration = "GM 开场：所有席位已经到场。"
        };
        await services.CampaignScenarios.UpsertAsync(scenario);
        var campaign = new Campaign
        {
            StoryId = scenario.Id,
            Title = scenario.Title,
            WorldSetting = scenario.WorldSetting,
            Rules = scenario.PublicRules,
            OpeningPrompt = scenario.OpeningSetup,
            UserAlsoPlayer = false,
            GmKind = CampaignGmKind.User,
            FlowPreset = CampaignFlowPreset.CollaborativeTable
        };
        var first = CreateAiParticipant(campaign.Id, 0, "玩家A", "model-a");
        var second = CreateAiParticipant(campaign.Id, 1, "玩家B", "model-b");
        await services.Campaigns.SaveDraftAsync(campaign, [first, second]);
        var gateway = new RecordingCampaignGateway();
        var runner = new CampaignRunner(
            services.Campaigns,
            services.CampaignScenarios,
            gateway,
            services.GenerationCoordinator,
            services.GlobalPrompts);

        await runner.StartAsync(campaign.Id);
        await runner.GenerateAiActionAsync(campaign.Id, first.Id);
        await runner.GenerateAiActionAsync(campaign.Id, second.Id);
        await runner.SubmitUserGmResolutionAsync(
            campaign.Id,
            "第一轮结果已经由 GM 裁定。");
        await runner.GenerateAiActionAsync(campaign.Id, first.Id);
        await runner.GenerateAiActionAsync(campaign.Id, second.Id);

        var payload = gateway.Requests
            .Last(request => request.ModelId == "model-b")
            .Messages[^1]
            .Content;
        var confirmedStart = payload.IndexOf(
            "【已裁定共同历史】",
            StringComparison.Ordinal);
        var latestGmStart = payload.IndexOf(
            "【最新 GM 场景与裁定｜当前行动依据】",
            StringComparison.Ordinal);
        var pendingStart = payload.IndexOf(
            "【本轮其他席位已提交内容｜结果等待 GM 裁定】",
            StringComparison.Ordinal);
        var taskStart = payload.IndexOf(
            "【当前回合任务】",
            StringComparison.Ordinal);
        Assert.True(
            0 <= confirmedStart
            && confirmedStart < latestGmStart
            && latestGmStart < pendingStart
            && pendingStart < taskStart);

        var confirmed = payload[confirmedStart..latestGmStart];
        var latestGm = payload[latestGmStart..pendingStart];
        var pending = payload[pendingStart..taskStart];
        Assert.True(
            confirmed.IndexOf("GM 开场：所有席位已经到场。", StringComparison.Ordinal)
            < confirmed.IndexOf("model-a 的公开行动。", StringComparison.Ordinal));
        Assert.True(
            confirmed.IndexOf("model-a 的公开行动。", StringComparison.Ordinal)
            < confirmed.IndexOf("model-b 的公开行动。", StringComparison.Ordinal));
        Assert.Contains(
            "\"resolution_status\":\"resolved_round_record\"",
            confirmed,
            StringComparison.Ordinal);
        Assert.Contains(
            "第一轮结果已经由 GM 裁定。",
            latestGm,
            StringComparison.Ordinal);
        Assert.Contains("model-a 的公开行动。", pending, StringComparison.Ordinal);
        Assert.DoesNotContain("model-b 的公开行动。", pending, StringComparison.Ordinal);
        Assert.Contains(
            "\"resolution_status\":\"pending_gm_resolution\"",
            pending,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task BlindSubmissionRunsDifferentModelsConcurrentlyWithoutCrossingPrompts()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var scenario = new CampaignScenario
        {
            Title = "秘密行动",
            WorldSetting = "封闭据点",
            PublicRules = "同轮行动彼此不可见",
            OpeningSetup = "警报响起",
            OpeningNarration = "警报响起，所有人必须秘密决定下一步。"
        };
        await services.CampaignScenarios.UpsertAsync(scenario);
        var campaign = new Campaign
        {
            StoryId = scenario.Id,
            Title = scenario.Title,
            WorldSetting = scenario.WorldSetting,
            Rules = scenario.PublicRules,
            OpeningPrompt = scenario.OpeningSetup,
            UserAlsoPlayer = false,
            GmKind = CampaignGmKind.User,
            FlowPreset = CampaignFlowPreset.BlindSubmission
        };
        var first = CreateAiParticipant(campaign.Id, 0, "A", "model-a");
        var second = CreateAiParticipant(campaign.Id, 1, "B", "model-b");
        await services.Campaigns.SaveDraftAsync(campaign, [first, second]);
        var gateway = new BarrierCampaignGateway();
        var runner = new CampaignRunner(
            services.Campaigns,
            services.CampaignScenarios,
            gateway,
            services.GenerationCoordinator,
            services.GlobalPrompts);
        await runner.StartAsync(campaign.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.GenerateAiActionAsync(campaign.Id, first.Id));
        var results = await runner.GenerateAiActionsAsync(campaign.Id);

        Assert.Equal(2, results.Count);
        Assert.All(results, item =>
        {
            Assert.Equal(CampaignGenerationStatus.Completed, item.GenerationStatus);
            Assert.Equal(CampaignVisibility.GmOnly, item.Visibility);
            AssertAutomaticActionRoll(item);
        });
        Assert.Equal(
            ["model-a", "model-b"],
            gateway.Requests.Select(item => item.ModelId).Order().ToArray());
        foreach (var request in gateway.Requests.Take(2))
        {
            var prompt = string.Join("\n", request.Messages.Select(item => item.Content));
            Assert.DoesNotContain("model-a 的秘密决定", prompt);
            Assert.DoesNotContain("model-b 的秘密决定", prompt);
        }

        var ready = await services.Campaigns.GetAsync(campaign.Id);
        Assert.Equal(CampaignPhase.ReadyForResolution, ready!.Campaign.Phase);

        await runner.SubmitUserGmResolutionAsync(
            campaign.Id,
            "秘密行动已经同时结算。");
        var secondRound = await runner.GenerateAiActionsAsync(campaign.Id);

        Assert.Equal(2, secondRound.Count);
        Assert.Equal(4, gateway.Requests.Count);
        foreach (var request in gateway.Requests.Skip(2))
        {
            var prompt = string.Join(
                "\n",
                request.Messages.Select(item => item.Content));
            Assert.Contains(
                "model-a 的秘密决定",
                prompt,
                StringComparison.Ordinal);
            Assert.Contains(
                "model-b 的秘密决定",
                prompt,
                StringComparison.Ordinal);
            Assert.Equal(
                2,
                CountOccurrences(prompt, "【随行动评定骰】"));
        }
    }

    [Fact]
    public async Task CollaborativeTableCanGenerateChosenAiSeatsInEitherOrder()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var scenario = new CampaignScenario
        {
            Title = "协作圆桌",
            WorldSetting = "调查现场",
            PublicRules = "行动按公开提交顺序进入上下文",
            OpeningSetup = "现场封锁",
            OpeningNarration = "两名调查员进入现场。"
        };
        await services.CampaignScenarios.UpsertAsync(scenario);
        var campaign = new Campaign
        {
            StoryId = scenario.Id,
            Title = scenario.Title,
            WorldSetting = scenario.WorldSetting,
            Rules = scenario.PublicRules,
            OpeningPrompt = scenario.OpeningSetup,
            UserAlsoPlayer = false,
            GmKind = CampaignGmKind.User,
            FlowPreset = CampaignFlowPreset.CollaborativeTable
        };
        var first = CreateAiParticipant(campaign.Id, 0, "A", "model-a");
        var second = CreateAiParticipant(campaign.Id, 1, "B", "model-b");
        await services.Campaigns.SaveDraftAsync(campaign, [first, second]);
        var gateway = new RecordingCampaignGateway();
        var runner = new CampaignRunner(
            services.Campaigns,
            services.CampaignScenarios,
            gateway,
            services.GenerationCoordinator,
            services.GlobalPrompts);
        await runner.StartAsync(campaign.Id);

        var secondAction = await runner.GenerateAiActionAsync(
            campaign.Id,
            second.Id);
        var firstAction = await runner.GenerateAiActionAsync(
            campaign.Id,
            first.Id);

        Assert.Equal(CampaignGenerationStatus.Completed, secondAction.GenerationStatus);
        Assert.Equal(CampaignGenerationStatus.Completed, firstAction.GenerationStatus);
        Assert.Equal(
            [second.Id, first.Id],
            gateway.Requests.Select(request =>
                request.SessionId!.Split(':')[^1]).ToArray());
        Assert.Contains(
            "model-b 的公开行动",
            gateway.Requests[1].Messages[^1].Content,
            StringComparison.Ordinal);
        var ready = await services.Campaigns.GetAsync(campaign.Id);
        Assert.Equal(CampaignPhase.ReadyForResolution, ready!.Campaign.Phase);
    }

    [Fact]
    public async Task FailedAiSeatMustRetryOriginalAttemptBeforeRoundCanContinue()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var scenario = new CampaignScenario
        {
            Title = "失败重试",
            WorldSetting = "测试场景",
            PublicRules = "失败不能跳过",
            OpeningSetup = "开始",
            OpeningNarration = "测试开始。"
        };
        await services.CampaignScenarios.UpsertAsync(scenario);
        var campaign = new Campaign
        {
            StoryId = scenario.Id,
            Title = scenario.Title,
            WorldSetting = scenario.WorldSetting,
            Rules = scenario.PublicRules,
            OpeningPrompt = scenario.OpeningSetup,
            UserAlsoPlayer = false,
            GmKind = CampaignGmKind.User,
            FlowPreset = CampaignFlowPreset.CollaborativeTable
        };
        var ai = CreateAiParticipant(campaign.Id, 0, "A", "model-a");
        await services.Campaigns.SaveDraftAsync(campaign, [ai]);
        var gateway = new FirstCallFailsCampaignGateway();
        var runner = new CampaignRunner(
            services.Campaigns,
            services.CampaignScenarios,
            gateway,
            services.GenerationCoordinator,
            services.GlobalPrompts);
        await runner.StartAsync(campaign.Id);

        var failed = await runner.GenerateAiActionAsync(campaign.Id, ai.Id);

        Assert.Equal(CampaignGenerationStatus.Failed, failed.GenerationStatus);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.GenerateAiActionAsync(campaign.Id, ai.Id));
        Assert.Empty(await runner.GenerateAiActionsAsync(campaign.Id));
        var stillWaiting = await services.Campaigns.GetAsync(campaign.Id);
        Assert.Equal(
            CampaignPhase.AwaitingActions,
            stillWaiting!.Campaign.Phase);

        var retried = await runner.RetryAiActionAsync(campaign.Id, failed.Id);

        Assert.Equal(CampaignGenerationStatus.Completed, retried.GenerationStatus);
        Assert.Equal(failed.Id, retried.ReplacesEventId);
        Assert.DoesNotContain(
            "【随行动评定骰】",
            failed.Content,
            StringComparison.Ordinal);
        AssertAutomaticActionRoll(retried);
        var ready = await services.Campaigns.GetAsync(campaign.Id);
        Assert.Equal(CampaignPhase.ReadyForResolution, ready!.Campaign.Phase);
    }

    [Fact]
    public async Task AiGmProtocolViolationRetainsOutputAndRetryAdvancesOnlyAfterValidTail()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var scenario = new CampaignScenario
        {
            Title = "GM 协议校验",
            WorldSetting = "测试场景",
            PublicRules = "GM 综合裁定",
            OpeningSetup = "开始",
            OpeningNarration = "测试开始。"
        };
        await services.CampaignScenarios.UpsertAsync(scenario);
        var campaign = new Campaign
        {
            StoryId = scenario.Id,
            Title = scenario.Title,
            WorldSetting = scenario.WorldSetting,
            Rules = scenario.PublicRules,
            OpeningPrompt = scenario.OpeningSetup,
            GmKind = CampaignGmKind.Ai,
            FlowPreset = CampaignFlowPreset.CollaborativeTable,
            GmProviderId = "openrouter",
            GmModelId = "gm-model"
        };
        var user = new CampaignParticipant
        {
            CampaignId = campaign.Id,
            Kind = CampaignParticipantKind.User,
            SortIndex = 0,
            DisplayName = "USER",
            PersonaSnapshotJson = """{"name":"USER"}"""
        };
        await services.Campaigns.SaveDraftAsync(campaign, [user]);
        var gateway = new GmProtocolRetryGateway();
        var runner = new CampaignRunner(
            services.Campaigns,
            services.CampaignScenarios,
            gateway,
            services.GenerationCoordinator,
            services.GlobalPrompts);
        await runner.StartAsync(campaign.Id);
        await runner.SubmitUserActionAsync(campaign.Id, "我观察门后的动静。");

        var failed = await runner.GenerateGmResolutionAsync(campaign.Id);

        Assert.Equal(CampaignGenerationStatus.Failed, failed.GenerationStatus);
        Assert.Equal(CampaignEndReason.ProtocolViolation, failed.EndReason);
        Assert.False(failed.IsLocked);
        Assert.Equal("门后传来脚步声。", failed.Content);
        Assert.Equal(1, failed.AttemptNo);
        Assert.Null(failed.ReplacesEventId);
        var stillReady = await services.Campaigns.GetAsync(campaign.Id);
        Assert.Equal(CampaignPhase.ReadyForResolution, stillReady!.Campaign.Phase);
        Assert.Equal(1, stillReady.Campaign.CurrentRound);

        var retried = await runner.GenerateGmResolutionAsync(campaign.Id);

        Assert.Equal(CampaignGenerationStatus.Completed, retried.GenerationStatus);
        Assert.Equal(CampaignEndReason.Normal, retried.EndReason);
        Assert.True(retried.IsLocked);
        Assert.Equal(2, retried.AttemptNo);
        Assert.Equal(failed.Id, retried.ReplacesEventId);
        var nextRound = await services.Campaigns.GetAsync(campaign.Id);
        Assert.Equal(CampaignPhase.AwaitingActions, nextRound!.Campaign.Phase);
        Assert.Equal(2, nextRound.Campaign.CurrentRound);
        var retained = Assert.Single(
            nextRound.Events,
            item => item.Id == failed.Id);
        Assert.Equal("门后传来脚步声。", retained.Content);
        Assert.Equal(CampaignGenerationStatus.Failed, retained.GenerationStatus);
    }

    [Fact]
    public async Task PendingUserJoinsOnlyAtNextFullStrictInitiativeRound()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var scenario = new CampaignScenario
        {
            Title = "中途加入",
            WorldSetting = "测试场景",
            PublicRules = "严格先攻",
            OpeningSetup = "开始",
            OpeningNarration = "两名 AI 依次行动。"
        };
        await services.CampaignScenarios.UpsertAsync(scenario);
        var campaign = new Campaign
        {
            StoryId = scenario.Id,
            Title = scenario.Title,
            WorldSetting = scenario.WorldSetting,
            Rules = scenario.PublicRules,
            OpeningPrompt = scenario.OpeningSetup,
            UserAlsoPlayer = false,
            UserPersonaName = "旅人",
            GmKind = CampaignGmKind.User,
            FlowPreset = CampaignFlowPreset.StrictInitiative
        };
        var first = CreateAiParticipant(campaign.Id, 0, "A", "model-a");
        var second = CreateAiParticipant(campaign.Id, 1, "B", "model-b");
        await services.Campaigns.SaveDraftAsync(campaign, [first, second]);
        var gateway = new RecordingCampaignGateway();
        var runner = new CampaignRunner(
            services.Campaigns,
            services.CampaignScenarios,
            gateway,
            services.GenerationCoordinator,
            services.GlobalPrompts);
        var started = await runner.StartAsync(campaign.Id);
        await services.Campaigns.ScheduleUserJoinAsync(
            campaign.Id,
            started.Campaign.StateVersion,
            "旅人",
            """{"name":"旅人"}""");

        await runner.GenerateAiActionAsync(campaign.Id, first.Id);
        await runner.SubmitUserGmResolutionAsync(campaign.Id, "A 的行动结束。");
        var midRound = await services.Campaigns.GetAsync(campaign.Id);

        Assert.Equal(1, midRound!.Campaign.CurrentRound);
        Assert.False(midRound.Campaign.UserAlsoPlayer);
        Assert.Contains(
            midRound.Participants,
            participant =>
                participant.Kind == CampaignParticipantKind.User
                && !participant.IsEnabled);

        await runner.GenerateAiActionAsync(campaign.Id, second.Id);
        await runner.SubmitUserGmResolutionAsync(campaign.Id, "B 的行动结束。");
        var nextRound = await services.Campaigns.GetAsync(campaign.Id);

        Assert.Equal(2, nextRound!.Campaign.CurrentRound);
        Assert.True(nextRound.Campaign.UserAlsoPlayer);
        var user = Assert.Single(
            nextRound.Participants,
            participant =>
                participant.Kind == CampaignParticipantKind.User);
        Assert.True(user.IsEnabled);
        Assert.Equal(0, user.SortIndex);
        Assert.Equal(
            ["旅人", "A", "B"],
            nextRound.Participants
                .Where(participant => participant.IsEnabled)
                .OrderBy(participant => participant.SortIndex)
                .Select(participant => participant.DisplayName)
                .ToArray());
        Assert.Contains(
            nextRound.Events,
            campaignEvent =>
                campaignEvent.Kind == CampaignEventKind.System
                && campaignEvent.Content.Contains(
                    "已从本回合起作为 USER 玩家加入",
                    StringComparison.Ordinal));
    }

    private static CampaignParticipant CreateAiParticipant(
        string campaignId,
        int sortIndex,
        string name,
        string modelId) =>
        new()
        {
            CampaignId = campaignId,
            Kind = CampaignParticipantKind.Ai,
            SortIndex = sortIndex,
            DisplayName = name,
            CharacterSnapshotJson =
                $"{{\"identity\":{{\"name\":\"{name}\"}}}}",
            ProviderId = "openrouter",
            ModelId = modelId
        };

    private static int AssertAutomaticActionRoll(CampaignEvent campaignEvent)
    {
        Assert.Equal(CampaignEventKind.PlayerIntent, campaignEvent.Kind);
        using var document = JsonDocument.Parse(campaignEvent.StructuredDataJson);
        var root = document.RootElement;
        Assert.Equal(
            "taverndesk.campaign-action-roll.v1",
            root.GetProperty("schema").GetString());
        Assert.Equal("1d20", root.GetProperty("expression").GetString());
        Assert.Equal(0, root.GetProperty("modifier").GetInt32());
        Assert.Equal(
            "fiction-flexible",
            root.GetProperty("interpretation").GetString());
        var total = root.GetProperty("total").GetInt32();
        Assert.InRange(total, 1, 20);
        var roll = Assert.Single(root.GetProperty("rolls").EnumerateArray());
        Assert.Equal(total, roll.GetInt32());
        Assert.EndsWith(
            $"【随行动评定骰】1d20 → [{total}] = {total}",
            campaignEvent.Content,
            StringComparison.Ordinal);
        return total;
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(
                   search,
                   index,
                   StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += search.Length;
        }

        return count;
    }

    private sealed class RecordingCampaignGateway : IProviderGateway
    {
        public List<ModelExecutionRequest> Requests { get; } = [];

        public Task<IReadOnlyList<ProviderModelDescriptor>> RefreshModelsAsync(
            string providerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProviderModelDescriptor>>([]);

        public async IAsyncEnumerable<ProviderStreamEvent> StreamChatAsync(
            ModelExecutionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ProviderStreamEvent(
                ProviderStreamEventKind.Content,
                request.ModelId == "gm-model"
                    ? """
                      GM 判定封印暂时稳定，但远处出现追兵。

                      【下一轮评定参考】
                      追兵逼近会增加时间压力，但玩家仍可自由决定调查、交涉、转移或其他行动。
                      """
                    : $"{request.ModelId} 的公开行动。");
            yield return new ProviderStreamEvent(
                ProviderStreamEventKind.Completed,
                FinishReason: "stop");
        }
    }

    private sealed class FirstCallFailsCampaignGateway : IProviderGateway
    {
        private int _callCount;

        public Task<IReadOnlyList<ProviderModelDescriptor>> RefreshModelsAsync(
            string providerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProviderModelDescriptor>>([]);

        public async IAsyncEnumerable<ProviderStreamEvent> StreamChatAsync(
            ModelExecutionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _callCount) == 1)
            {
                yield return new ProviderStreamEvent(
                    ProviderStreamEventKind.Completed,
                    FinishReason: "length");
                yield break;
            }

            yield return new ProviderStreamEvent(
                ProviderStreamEventKind.Content,
                "重试后成功完成行动。");
            yield return new ProviderStreamEvent(
                ProviderStreamEventKind.Completed,
                FinishReason: "stop");
        }
    }

    private sealed class GmProtocolRetryGateway : IProviderGateway
    {
        private int _callCount;

        public Task<IReadOnlyList<ProviderModelDescriptor>> RefreshModelsAsync(
            string providerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProviderModelDescriptor>>([]);

        public async IAsyncEnumerable<ProviderStreamEvent> StreamChatAsync(
            ModelExecutionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            var content = Interlocked.Increment(ref _callCount) == 1
                ? "门后传来脚步声。"
                : """
                  门后传来脚步声，但尚未有人推门。

                  【下一轮评定参考】
                  声音的距离、掩护与玩家接下来的做法都可能改变风险。
                  """;
            yield return new ProviderStreamEvent(
                ProviderStreamEventKind.Content,
                content);
            yield return new ProviderStreamEvent(
                ProviderStreamEventKind.Completed,
                FinishReason: "stop");
        }
    }

    private sealed class BarrierCampaignGateway : IProviderGateway
    {
        private readonly TaskCompletionSource _bothEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _entered;

        public List<ModelExecutionRequest> Requests { get; } = [];

        public Task<IReadOnlyList<ProviderModelDescriptor>> RefreshModelsAsync(
            string providerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProviderModelDescriptor>>([]);

        public async IAsyncEnumerable<ProviderStreamEvent> StreamChatAsync(
            ModelExecutionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            lock (Requests)
            {
                Requests.Add(request);
            }

            if (Interlocked.Increment(ref _entered) == 2)
            {
                _bothEntered.TrySetResult();
            }

            await _bothEntered.Task.WaitAsync(cancellationToken);
            yield return new ProviderStreamEvent(
                ProviderStreamEventKind.Content,
                $"{request.ModelId} 的秘密决定");
            yield return new ProviderStreamEvent(
                ProviderStreamEventKind.Completed,
                FinishReason: "stop");
        }
    }
}
