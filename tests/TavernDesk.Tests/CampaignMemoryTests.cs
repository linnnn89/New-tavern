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
        InfrastructureServices services)
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
            GmKind = CampaignGmKind.User
        };
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

    private static async Task AppendLockedEventAsync(
        InfrastructureServices services,
        string campaignId,
        CampaignEventKind kind,
        string content,
        CampaignVisibility visibility,
        string operationId)
    {
        await services.Campaigns.AppendEventAsync(new CampaignEvent
        {
            CampaignId = campaignId,
            RoundNo = 1,
            Kind = kind,
            ActorId = kind == CampaignEventKind.GmResolution ? "gm:user" : "测试玩家",
            Visibility = visibility,
            Content = content,
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
