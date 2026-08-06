using System.Text.Json;
using System.Text.RegularExpressions;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;
using TavernDesk.Infrastructure;
using TavernDesk.Infrastructure.Campaigns;

namespace TavernDesk.Tests;

public sealed class CampaignMemoryTests
{
    [Fact]
    public async Task DisabledCampaignMemoryNeverCallsProviderOrCreatesCheckpoint()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var campaign = await CreateActiveCampaignAsync(
            services,
            configure: item => item.MemoryEnabled = false);
        var resolution = await AppendLockedEventAsync(
            services,
            campaign.Id,
            CampaignEventKind.GmResolution,
            "disabled memory resolution",
            CampaignVisibility.Public,
            "disabled-resolution",
            roundNo: 1);
        var gateway = new RecordingMemoryGateway();
        var service = new CampaignMemoryUpdateService(
            services.Campaigns,
            services.CampaignMemoryRepository,
            services.ModelAssignments,
            gateway,
            services.GenerationCoordinator);

        var result = await service.UpdateAsync(
            campaign.Id,
            resolution.SequenceNo,
            force: true);

        Assert.Equal(CampaignMemoryUpdateStatus.NoChanges, result.Status);
        Assert.Empty(gateway.Requests);
        Assert.Null(await services.CampaignMemoryRepository.GetBankAsync(
            campaign.Id,
            CampaignMemoryScope.GameMaster));
        Assert.Null(await services.CampaignMemoryRepository.GetCheckpointAsync(
            campaign.Id,
            CampaignMemoryScope.Public));
    }

    [Fact]
    public async Task AutomaticUpdateWaitsForThreeCompleteRoundsAndStopsAtResolutionBoundary()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var campaign = await CreateActiveCampaignAsync(services);
        await ConfigureMemoryAssignmentAsync(services);
        var gateway = new RecordingMemoryGateway();
        var service = new CampaignMemoryUpdateService(
            services.Campaigns,
            services.CampaignMemoryRepository,
            services.ModelAssignments,
            gateway,
            services.GenerationCoordinator);

        long lastResolutionSequence = 0;
        for (var round = 1; round <= 2; round++)
        {
            await AppendLockedEventAsync(
                services,
                campaign.Id,
                CampaignEventKind.PlayerIntent,
                $"第 {round} 轮行动",
                CampaignVisibility.Public,
                $"action-{round}",
                roundNo: round);
            var resolution = await AppendLockedEventAsync(
                services,
                campaign.Id,
                CampaignEventKind.GmResolution,
                $"第 {round} 轮裁定",
                CampaignVisibility.Public,
                $"resolution-{round}",
                roundNo: round);
            lastResolutionSequence = resolution.SequenceNo;

            var result = await service.UpdateAsync(
                campaign.Id,
                lastResolutionSequence);
            Assert.Equal(CampaignMemoryUpdateStatus.NoChanges, result.Status);
            Assert.Empty(gateway.Requests);
        }

        await AppendLockedEventAsync(
            services,
            campaign.Id,
            CampaignEventKind.PlayerIntent,
            "第 3 轮行动",
            CampaignVisibility.Public,
            "action-3",
            roundNo: 3);
        var thirdResolution = await AppendLockedEventAsync(
            services,
            campaign.Id,
            CampaignEventKind.GmResolution,
            "第 3 轮裁定",
            CampaignVisibility.Public,
            "resolution-3",
            roundNo: 3);
        await AppendLockedEventAsync(
            services,
            campaign.Id,
            CampaignEventKind.PlayerIntent,
            "下一轮尚未裁定，不应进入本次记忆",
            CampaignVisibility.Public,
            "action-4",
            roundNo: 4);

        var updated = await service.UpdateAsync(
            campaign.Id,
            thirdResolution.SequenceNo);

        Assert.Equal(CampaignMemoryUpdateStatus.Updated, updated.Status);
        Assert.Equal(thirdResolution.SequenceNo, updated.SourceThroughEventSequence);
        Assert.Equal(2, gateway.Requests.Count);
        Assert.DoesNotContain(
            "下一轮尚未裁定",
            gateway.Requests[0].Messages[1].Content,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "下一轮尚未裁定",
            gateway.Requests[1].Messages[1].Content,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AutomaticUpdateUsesPendingTokenThresholdBeforeRoundInterval()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var campaign = await CreateActiveCampaignAsync(
            services,
            configure: item =>
            {
                item.MemoryUpdateIntervalRounds = 50;
                item.MemoryUpdatePendingTokenThreshold = 1_000;
            });
        await ConfigureMemoryAssignmentAsync(services);
        var gateway = new RecordingMemoryGateway();
        var service = new CampaignMemoryUpdateService(
            services.Campaigns,
            services.CampaignMemoryRepository,
            services.ModelAssignments,
            gateway,
            services.GenerationCoordinator);
        await AppendLockedEventAsync(
            services,
            campaign.Id,
            CampaignEventKind.PlayerIntent,
            new string('长', 6_000),
            CampaignVisibility.Public,
            "large-action",
            roundNo: 1);
        var resolution = await AppendLockedEventAsync(
            services,
            campaign.Id,
            CampaignEventKind.GmResolution,
            "大输入裁定",
            CampaignVisibility.Public,
            "large-resolution",
            roundNo: 1);

        var result = await service.UpdateAsync(campaign.Id, resolution.SequenceNo);

        Assert.Equal(CampaignMemoryUpdateStatus.Updated, result.Status);
        Assert.Equal(2, gateway.Requests.Count);
    }

    [Fact]
    public async Task BlindSubmissionIsPublicOnlyAfterResolutionAndGmPayloadKeepsRecipientMetadata()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var campaign = await CreateActiveCampaignAsync(
            services,
            flowPreset: CampaignFlowPreset.BlindSubmission);
        var participant = (await services.Campaigns.GetAsync(campaign.Id))!
            .Participants
            .Single();
        await ConfigureMemoryAssignmentAsync(services);
        var gateway = new RecordingMemoryGateway();
        var service = new CampaignMemoryUpdateService(
            services.Campaigns,
            services.CampaignMemoryRepository,
            services.ModelAssignments,
            gateway,
            services.GenerationCoordinator);
        await AppendLockedEventAsync(
            services,
            campaign.Id,
            CampaignEventKind.PlayerIntent,
            "秘密行动在裁定后公开",
            CampaignVisibility.Private,
            "blind-action",
            roundNo: 1,
            actorId: participant.Id,
            recipientId: participant.Id,
            structuredDataJson: "{\"roll\":20}");
        await AppendLockedEventAsync(
            services,
            campaign.Id,
            CampaignEventKind.PrivateDelivery,
            "只给该玩家的私密投递",
            CampaignVisibility.Private,
            "private-delivery",
            roundNo: 1,
            actorId: "gm:user",
            recipientId: participant.Id,
            structuredDataJson: "{\"delivery\":\"secret\"}");
        var resolution = await AppendLockedEventAsync(
            services,
            campaign.Id,
            CampaignEventKind.GmResolution,
            "秘密行动的公开裁定",
            CampaignVisibility.Public,
            "blind-resolution",
            roundNo: 1,
            actorId: "gm:user");

        var result = await service.UpdateAsync(
            campaign.Id,
            resolution.SequenceNo,
            force: true);

        Assert.Equal(CampaignMemoryUpdateStatus.Updated, result.Status);
        Assert.Equal(2, gateway.Requests.Count);
        var gmInput = gateway.Requests[0].Messages[1].Content;
        var publicInput = gateway.Requests[1].Messages[1].Content;
        Assert.Contains("recipient_id", gmInput, StringComparison.Ordinal);
        Assert.Contains(participant.Id, gmInput, StringComparison.Ordinal);
        Assert.Contains("structured_data", gmInput, StringComparison.Ordinal);
        Assert.Contains("秘密行动在裁定后公开", publicInput, StringComparison.Ordinal);
        Assert.DoesNotContain("只给该玩家的私密投递", publicInput, StringComparison.Ordinal);
        Assert.DoesNotContain("recipient_id", publicInput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonResolutionBoundaryFailsWithoutCallingProvider()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var campaign = await CreateActiveCampaignAsync(services);
        var action = await AppendLockedEventAsync(
            services,
            campaign.Id,
            CampaignEventKind.PlayerIntent,
            "只是一条行动",
            CampaignVisibility.Public,
            "boundary-action");
        await ConfigureMemoryAssignmentAsync(services);
        var gateway = new RecordingMemoryGateway();
        var service = new CampaignMemoryUpdateService(
            services.Campaigns,
            services.CampaignMemoryRepository,
            services.ModelAssignments,
            gateway,
            services.GenerationCoordinator);

        var result = await service.UpdateAsync(campaign.Id, action.SequenceNo);

        Assert.Equal(CampaignMemoryUpdateStatus.Failed, result.Status);
        Assert.Empty(gateway.Requests);
        Assert.Null(await services.CampaignMemoryRepository.GetCheckpointAsync(
            campaign.Id,
            CampaignMemoryScope.GameMaster));
    }

    [Fact]
    public async Task UpdateSeparatesPublicMemoryAndAdvancesBothCheckpoints()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var campaign = await CreateActiveCampaignAsync(services);
        await AppendLockedEventAsync(
            services,
            campaign.Id,
            CampaignEventKind.GmOpening,
            "公开开场",
            CampaignVisibility.Public,
            "opening");
        await AppendLockedEventAsync(
            services,
            campaign.Id,
            CampaignEventKind.StateDelta,
            "SECRET_BACKSTAGE",
            CampaignVisibility.GmOnly,
            "secret");
        await AppendLockedEventAsync(
            services,
            campaign.Id,
            CampaignEventKind.PlayerIntent,
            "PRIVATE_PLAN",
            CampaignVisibility.Private,
            "private");
        await AppendLockedEventAsync(
            services,
            campaign.Id,
            CampaignEventKind.GmResolution,
            "PUBLIC_RESULT",
            CampaignVisibility.Public,
            "resolution");
        var expectedSequence = (await services.Campaigns.GetAsync(campaign.Id))!
            .Events
            .Max(item => item.SequenceNo);
        await ConfigureMemoryAssignmentAsync(services);

        var gateway = new RecordingMemoryGateway();
        var service = new CampaignMemoryUpdateService(
            services.Campaigns,
            services.CampaignMemoryRepository,
            services.ModelAssignments,
            gateway,
            services.GenerationCoordinator);

        var result = await service.UpdateAsync(campaign.Id);

        Assert.Equal(CampaignMemoryUpdateStatus.Updated, result.Status);
        Assert.Equal(expectedSequence, result.SourceThroughEventSequence);
        Assert.Equal(2, gateway.Requests.Count);
        var gmInput = gateway.Requests[0].Messages[1].Content;
        var publicInput = gateway.Requests[1].Messages[1].Content;
        Assert.Contains("SECRET_BACKSTAGE", gmInput, StringComparison.Ordinal);
        Assert.Contains("PRIVATE_PLAN", gmInput, StringComparison.Ordinal);
        Assert.Contains("PUBLIC_RESULT", publicInput, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET_BACKSTAGE", publicInput, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE_PLAN", publicInput, StringComparison.Ordinal);

        var gmBank = await services.CampaignMemoryRepository.GetBankAsync(
            campaign.Id,
            CampaignMemoryScope.GameMaster);
        var publicBank = await services.CampaignMemoryRepository.GetBankAsync(
            campaign.Id,
            CampaignMemoryScope.Public);
        var gmCheckpoint = await services.CampaignMemoryRepository.GetCheckpointAsync(
            campaign.Id,
            CampaignMemoryScope.GameMaster);
        var publicCheckpoint = await services.CampaignMemoryRepository.GetCheckpointAsync(
            campaign.Id,
            CampaignMemoryScope.Public);
        Assert.Equal("GM_MEMORY", gmBank?.Body);
        Assert.Equal("PUBLIC_MEMORY", publicBank?.Body);
        Assert.Equal(expectedSequence, gmCheckpoint?.LastEventSequence);
        Assert.Equal(expectedSequence, publicCheckpoint?.LastEventSequence);

        var second = await service.UpdateAsync(campaign.Id);
        Assert.Equal(CampaignMemoryUpdateStatus.NoChanges, second.Status);
        Assert.Equal(2, gateway.Requests.Count);
    }

    [Fact]
    public async Task InvalidMemoryOutputDoesNotAdvanceCheckpoint()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var campaign = await CreateActiveCampaignAsync(services);
        await AppendLockedEventAsync(
            services,
            campaign.Id,
            CampaignEventKind.GmResolution,
            "必须进入事件账本",
            CampaignVisibility.Public,
            "resolution");
        await ConfigureMemoryAssignmentAsync(services);

        var service = new CampaignMemoryUpdateService(
            services.Campaigns,
            services.CampaignMemoryRepository,
            services.ModelAssignments,
            new RecordingMemoryGateway { ReturnInvalidJson = true },
            services.GenerationCoordinator);

        var result = await service.UpdateAsync(campaign.Id);

        Assert.Equal(CampaignMemoryUpdateStatus.Failed, result.Status);
        Assert.Null(await services.CampaignMemoryRepository.GetBankAsync(
            campaign.Id,
            CampaignMemoryScope.GameMaster));
        Assert.Null(await services.CampaignMemoryRepository.GetCheckpointAsync(
            campaign.Id,
            CampaignMemoryScope.GameMaster));
        Assert.Null(await services.CampaignMemoryRepository.GetCheckpointAsync(
            campaign.Id,
            CampaignMemoryScope.Public));
    }

    private static async Task<Campaign> CreateActiveCampaignAsync(
        InfrastructureServices services,
        CampaignFlowPreset flowPreset = CampaignFlowPreset.CollaborativeTable,
        Action<Campaign>? configure = null)
    {
        var scenario = new CampaignScenario
        {
            Title = "记忆测试剧本",
            WorldSetting = "测试世界",
            PublicRules = "测试规则",
            OpeningSetup = "测试开场"
        };
        await services.CampaignScenarios.UpsertAsync(scenario);
        var campaign = new Campaign
        {
            StoryId = scenario.Id,
            Title = "记忆测试局",
            WorldSetting = scenario.WorldSetting,
            Rules = scenario.PublicRules,
            OpeningPrompt = scenario.OpeningSetup,
            GmKind = CampaignGmKind.User,
            FlowPreset = flowPreset
        };
        configure?.Invoke(campaign);
        await services.Campaigns.SaveDraftAsync(
            campaign,
            [new CampaignParticipant
            {
                CampaignId = campaign.Id,
                Kind = CampaignParticipantKind.User,
                SortIndex = 0,
                DisplayName = "测试玩家"
            }]);
        await services.Campaigns.StartAsync(campaign.Id);
        return campaign;
    }

    private static async Task<CampaignEvent> AppendLockedEventAsync(
        InfrastructureServices services,
        string campaignId,
        CampaignEventKind kind,
        string content,
        CampaignVisibility visibility,
        string operationId,
        int roundNo = 1,
        string? actorId = null,
        string? recipientId = null,
        string structuredDataJson = "{}")
    {
        var resolvedActorId = actorId;
        if (resolvedActorId is null)
        {
            resolvedActorId = kind == CampaignEventKind.GmResolution
                ? "gm:user"
                : (await services.Campaigns.GetAsync(campaignId))!
                    .Participants
                    .First(item => item.IsEnabled)
                    .Id;
        }

        return await services.Campaigns.AppendEventAsync(new CampaignEvent
        {
            CampaignId = campaignId,
            RoundNo = roundNo,
            Kind = kind,
            ActorId = resolvedActorId,
            RecipientId = recipientId,
            Visibility = visibility,
            Content = content,
            StructuredDataJson = structuredDataJson,
            GenerationStatus = CampaignGenerationStatus.Completed,
            EndReason = CampaignEndReason.Normal,
            OperationId = operationId,
            IsLocked = true
        });
    }

    private static async Task ConfigureMemoryAssignmentAsync(
        InfrastructureServices services)
    {
        await services.Providers.UpsertAsync(new ProviderProfile
        {
            Id = "memory-fixture-provider",
            Name = "Memory Fixture Provider",
            BaseUrl = "https://fixture.invalid/v1"
        });
        await services.Models.ReplaceAsync(
            "memory-fixture-provider",
            [new ProviderModelDescriptor("memory-fixture-model", "Memory Fixture Model")]);
        await services.ModelAssignments.UpsertAsync(new ModelFunctionAssignment
        {
            FunctionKind = ModelFunctionKind.MemoryUpdate,
            ProviderId = "memory-fixture-provider",
            ModelId = "memory-fixture-model",
            ContextLimit = 32768,
            MaxOutputTokens = 1024
        });
    }

    private sealed class RecordingMemoryGateway : IProviderGateway
    {
        public List<ModelExecutionRequest> Requests { get; } = [];
        public bool ReturnInvalidJson { get; init; }

        public Task<IReadOnlyList<ProviderModelDescriptor>> RefreshModelsAsync(
            string providerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProviderModelDescriptor>>([]);

        public async IAsyncEnumerable<ProviderStreamEvent> StreamChatAsync(
            ModelExecutionRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await Task.CompletedTask;
            if (ReturnInvalidJson)
            {
                yield return new ProviderStreamEvent(
                    ProviderStreamEventKind.Content,
                    "not-json");
            }
            else
            {
                var input = request.Messages[^1].Content;
                var sequence = Regex.Match(
                        input,
                        @"sourceThroughEventSequence 必须为 (?<sequence>\d+)")
                    .Groups["sequence"]
                    .Value;
                var isPublic = input.Contains(
                    "【记忆范围】\n公共",
                    StringComparison.Ordinal);
                yield return new ProviderStreamEvent(
                    ProviderStreamEventKind.Content,
                    JsonSerializer.Serialize(new
                    {
                        body = isPublic ? "PUBLIC_MEMORY" : "GM_MEMORY",
                        importantFacts = Array.Empty<string>(),
                        openThreads = Array.Empty<string>(),
                        sourceThroughEventSequence = long.Parse(sequence)
                    }));
            }

            yield return new ProviderStreamEvent(
                ProviderStreamEventKind.Completed,
                FinishReason: "stop");
        }
    }
}
