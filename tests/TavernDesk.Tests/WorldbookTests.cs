using System.Text;
using System.Text.Json.Nodes;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;
using TavernDesk.Infrastructure;
using TavernDesk.Infrastructure.Worldbooks;

namespace TavernDesk.Tests;

public sealed class WorldbookTests
{
    [Fact]
    public async Task EntryTitleCanBeEditedInLocalCopyAndIndexedTextIncludesTitleAndKeys()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();

        var sourcePath = Path.Combine(workspace.Root, "editable-world-info.json");
        await File.WriteAllTextAsync(
            sourcePath,
            """
            {
              "name": "可编辑世界书",
              "entries": {
                "entry-1": {
                  "name": "旧词条名",
                  "keys": ["旧关键词"],
                  "content": "这段正文用于验证世界书索引。",
                  "enabled": true,
                  "constant": false,
                  "extensions": {"semanticEnabled": true}
                }
              }
            }
            """,
            Encoding.UTF8);

        var imported = await services.WorldbookService.ImportAsync(
            sourcePath,
            WorldbookScopeKind.Global,
            null);
        await services.WorldbookService.UpdateEntryTitleAsync(
            imported.Worldbook.Id,
            "entry-1",
            "新词条名");

        var savedEntry = Assert.Single(
            await services.WorldbookService.ListEntriesAsync(imported.Worldbook.Id));
        Assert.Equal("新词条名", savedEntry.Title);
        Assert.Contains("旧词条名", imported.Worldbook.RawJson, StringComparison.Ordinal);
        Assert.Equal(
            2,
            (await services.Worldbooks.GetAsync(imported.Worldbook.Id))!.Revision);

        await services.WorldbookService.RebuildIndexAsync(imported.Worldbook.Id);
        var chunk = Assert.Single(
            await services.Worldbooks.ListChunksAsync(
                new HashSet<string> { imported.Worldbook.Id }));
        Assert.Contains("新词条名", chunk.NormalizedContent, StringComparison.Ordinal);
        Assert.Contains("旧关键词", chunk.NormalizedContent, StringComparison.Ordinal);
        Assert.Contains("这段正文", chunk.NormalizedContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StandaloneObjectEntriesImportMountAndEnterKeywordContext()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();

        var sourcePath = Path.Combine(workspace.Root, "world-info.json");
        await File.WriteAllTextAsync(
            sourcePath,
            """
            {
              "name": "测试世界书",
              "description": "独立 world_info 示例",
              "scan_depth": 0,
              "recursive_scanning": true,
              "entries": {
                "17": {
                  "uid": 17,
                  "keys": ["黑曜石"],
                  "content": "黑曜石是这个世界中用于封印裂隙的矿石。",
                  "enabled": true,
                  "constant": false,
                  "position": 1,
                  "extensions": {
                    "semanticEnabled": true
                  }
                }
              }
            }
            """,
            Encoding.UTF8);

        var sourceText = await File.ReadAllTextAsync(sourcePath);
        var sourceRoot = JsonNode.Parse(sourceText)!.AsObject();
        Assert.False(sourceRoot.ContainsKey("spec"));
        var parsed = WorldbookJsonParser.Parse(sourceText);
        Assert.True(parsed.FoundBook);

        var imported = await services.WorldbookService.ImportAsync(
            sourcePath,
            WorldbookScopeKind.Global,
            null);

        Assert.Equal(WorldbookSourceKind.StandaloneJson, imported.Worldbook.SourceKind);
        Assert.Single(imported.Entries);
        Assert.Equal("17", imported.Entries[0].Id);
        var active = await services.WorldbookService.ListEnabledForCharacterAsync(null);
        Assert.Contains(active, item => item.Id == imported.Worldbook.Id);
        var index = await services.WorldbookService.RebuildIndexAsync(imported.Worldbook.Id);
        Assert.Equal(1, index.ChunkCount);
        var retrieved = await services.WorldbookService.RetrieveAsync(
            new WorldbookRetrievalRequest(
                "worldbook-test",
                null,
                "黑曜石用途",
                new Dictionary<string, string>()));
        Assert.Contains(retrieved.Matches, match => match.Content.Contains("封印裂隙", StringComparison.Ordinal));

        var character = new Character { Name = "世界书测试角色" };
        await services.Characters.UpsertAsync(character);
        var conversation = new Conversation
        {
            CharacterId = character.Id,
            Title = "世界书上下文测试"
        };
        await services.Conversations.UpsertAsync(conversation);

        var assembled = await services.ContextAssembler.AssembleAsync(
            new ContextAssemblyRequest(
                conversation.Id,
                "请解释黑曜石的用途",
                32768,
                4096));

        Assert.Contains(
            assembled.Segments,
            segment => segment.Kind == ContextSegmentKind.Worldbook
                       && segment.Content.Contains("封印裂隙", StringComparison.Ordinal));

        await services.Worldbooks.UpsertMountAsync(
            new WorldbookMount
            {
                WorldbookId = imported.Worldbook.Id,
                ScopeKind = WorldbookScopeKind.Character,
                ScopeId = character.Id,
                SortIndex = 100,
                IsEnabled = true,
                MountedRevision = imported.Worldbook.Revision
            });
        Assert.Single(
            await services.WorldbookService.ListEnabledForCharacterAsync(character.Id));
        var duplicateMountResult = await services.WorldbookService.RetrieveAsync(
            new WorldbookRetrievalRequest(
                "worldbook-test-character",
                character.Id,
                "黑曜石用途",
                new Dictionary<string, string>()));
        Assert.NotEmpty(duplicateMountResult.Matches);

        await services.WorldbookService.DeleteAsync(imported.Worldbook.Id);
        Assert.Empty(
            await services.Worldbooks.SearchTextAsync(
                new HashSet<string> { imported.Worldbook.Id },
                "黑曜石",
                10));
    }

    [Fact]
    public async Task EmbeddedCharacterCardWorldbookCanBeImportedWithCharacterMount()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var character = new Character { Name = "内置书绑定角色" };
        await services.Characters.UpsertAsync(character);

        var sourcePath = Path.Combine(workspace.Root, "embedded-card.json");
        await File.WriteAllTextAsync(
            sourcePath,
            """
            {
              "spec": "chara_card_v2",
              "spec_version": "2.0",
              "data": {
                "name": "带世界书的角色卡",
                "description": "角色卡说明",
                "character_book": {
                  "name": "内置测试世界书",
                  "entries": [
                    {
                      "id": "embedded-1",
                      "name": "裂隙规则",
                      "keys": ["裂隙"],
                      "content": "裂隙只能由守门人关闭。",
                      "enabled": true,
                      "constant": false
                    }
                  ]
                }
              }
            }
            """,
            Encoding.UTF8);

        var imported = await services.WorldbookService.ImportAsync(
            sourcePath,
            WorldbookScopeKind.Character,
            character.Id);

        Assert.Equal(WorldbookSourceKind.CharacterCardEmbedded, imported.Worldbook.SourceKind);
        var mounts = await services.Worldbooks.ListMountsAsync(imported.Worldbook.Id);
        var mount = Assert.Single(mounts);
        Assert.Equal(WorldbookScopeKind.Character, mount.ScopeKind);
        Assert.Equal(character.Id, mount.ScopeId);
    }

    [Fact]
    public async Task WorldbookCanBindMultipleCharactersWithoutChangingOtherMounts()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();

        var firstCharacter = new Character { Name = "第一个绑定角色" };
        var secondCharacter = new Character { Name = "第二个绑定角色" };
        await services.Characters.UpsertAsync(firstCharacter);
        await services.Characters.UpsertAsync(secondCharacter);

        var sourcePath = Path.Combine(workspace.Root, "multi-character-worldbook.json");
        await File.WriteAllTextAsync(
            sourcePath,
            """
            {
              "name": "多角色绑定世界书",
              "entries": {
                "1": {
                  "uid": 1,
                  "keys": ["多角色"],
                  "content": "这本世界书可以同时服务多个角色。",
                  "enabled": true,
                  "constant": false
                }
              }
            }
            """,
            Encoding.UTF8);
        var imported = await services.WorldbookService.ImportAsync(
            sourcePath,
            WorldbookScopeKind.Global,
            null);

        var globalMount = Assert.Single(
            await services.WorldbookService.ListMountsAsync(imported.Worldbook.Id));
        Assert.Equal(WorldbookScopeKind.Global, globalMount.ScopeKind);

        await services.WorldbookService.ReplaceCharacterMountsAsync(
            imported.Worldbook.Id,
            [
                new WorldbookMount
                {
                    WorldbookId = imported.Worldbook.Id,
                    ScopeKind = WorldbookScopeKind.Character,
                    ScopeId = firstCharacter.Id,
                    SortIndex = 100,
                    IsEnabled = true,
                    MountedRevision = imported.Worldbook.Revision
                },
                new WorldbookMount
                {
                    WorldbookId = imported.Worldbook.Id,
                    ScopeKind = WorldbookScopeKind.Character,
                    ScopeId = secondCharacter.Id,
                    SortIndex = 110,
                    IsEnabled = true,
                    MountedRevision = imported.Worldbook.Revision
                }
            ]);

        var mounts = await services.WorldbookService.ListMountsAsync(imported.Worldbook.Id);
        Assert.Equal(3, mounts.Count);
        Assert.Contains(
            mounts,
            mount => mount.ScopeKind == WorldbookScopeKind.Global);
        Assert.Contains(
            mounts,
            mount => mount.ScopeKind == WorldbookScopeKind.Character
                     && mount.ScopeId == firstCharacter.Id);
        Assert.Contains(
            mounts,
            mount => mount.ScopeKind == WorldbookScopeKind.Character
                     && mount.ScopeId == secondCharacter.Id);

        await services.WorldbookService.ReplaceCharacterMountsAsync(
            imported.Worldbook.Id,
            [
                new WorldbookMount
                {
                    WorldbookId = imported.Worldbook.Id,
                    ScopeKind = WorldbookScopeKind.Character,
                    ScopeId = secondCharacter.Id,
                    SortIndex = 110,
                    IsEnabled = true,
                    MountedRevision = imported.Worldbook.Revision
                }
            ]);

        mounts = await services.WorldbookService.ListMountsAsync(imported.Worldbook.Id);
        Assert.Equal(2, mounts.Count);
        Assert.DoesNotContain(
            mounts,
            mount => mount.ScopeKind == WorldbookScopeKind.Character
                     && mount.ScopeId == firstCharacter.Id);
        Assert.Contains(
            mounts,
            mount => mount.ScopeKind == WorldbookScopeKind.Character
                     && mount.ScopeId == secondCharacter.Id);
        Assert.Contains(
            mounts,
            mount => mount.ScopeKind == WorldbookScopeKind.Global);
    }

    [Fact]
    public async Task WorldbookCanBindAndUnbindMultipleCampaignScenariosWithoutChangingOtherMounts()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();

        var firstScenario = new CampaignScenario { Title = "第一个跑团剧本" };
        var secondScenario = new CampaignScenario { Title = "第二个跑团剧本" };
        await services.CampaignScenarios.UpsertAsync(firstScenario);
        await services.CampaignScenarios.UpsertAsync(secondScenario);

        var sourcePath = Path.Combine(workspace.Root, "campaign-worldbook.json");
        await File.WriteAllTextAsync(
            sourcePath,
            """
            {
              "name": "跑团绑定世界书",
              "entries": {
                "1": {
                  "uid": 1,
                  "keys": ["跑团规则"],
                  "content": "这本世界书只在绑定的跑团剧本中使用。",
                  "enabled": true,
                  "constant": false
                }
              }
            }
            """,
            Encoding.UTF8);

        var imported = await services.WorldbookService.ImportAsync(
            sourcePath,
            WorldbookScopeKind.Global,
            null);
        await services.WorldbookService.ImportAsync(
            sourcePath,
            WorldbookScopeKind.Campaign,
            firstScenario.Id);
        Assert.Contains(
            await services.WorldbookService.ListMountsAsync(imported.Worldbook.Id),
            mount => mount.ScopeKind == WorldbookScopeKind.Campaign
                     && mount.ScopeId == firstScenario.Id);
        await services.WorldbookService.ReplaceScopeMountsAsync(
            imported.Worldbook.Id,
            WorldbookScopeKind.Campaign,
            [
                new WorldbookMount
                {
                    WorldbookId = imported.Worldbook.Id,
                    ScopeKind = WorldbookScopeKind.Campaign,
                    ScopeId = firstScenario.Id,
                    SortIndex = 100,
                    IsEnabled = true,
                    MountedRevision = imported.Worldbook.Revision
                },
                new WorldbookMount
                {
                    WorldbookId = imported.Worldbook.Id,
                    ScopeKind = WorldbookScopeKind.Campaign,
                    ScopeId = secondScenario.Id,
                    SortIndex = 110,
                    IsEnabled = true,
                    MountedRevision = imported.Worldbook.Revision
                }
            ]);

        var mounts = await services.WorldbookService.ListMountsAsync(
            imported.Worldbook.Id);
        Assert.Equal(3, mounts.Count);
        Assert.Contains(mounts, mount => mount.ScopeKind == WorldbookScopeKind.Global);
        Assert.Equal(
            2,
            mounts.Count(mount => mount.ScopeKind == WorldbookScopeKind.Campaign));

        await services.WorldbookService.ReplaceScopeMountsAsync(
            imported.Worldbook.Id,
            WorldbookScopeKind.Campaign,
            [
                new WorldbookMount
                {
                    WorldbookId = imported.Worldbook.Id,
                    ScopeKind = WorldbookScopeKind.Campaign,
                    ScopeId = secondScenario.Id,
                    SortIndex = 100,
                    IsEnabled = true,
                    MountedRevision = imported.Worldbook.Revision
                }
            ]);

        mounts = await services.WorldbookService.ListMountsAsync(
            imported.Worldbook.Id);
        Assert.Equal(2, mounts.Count);
        Assert.DoesNotContain(
            mounts,
            mount => mount.ScopeKind == WorldbookScopeKind.Campaign
                     && mount.ScopeId == firstScenario.Id);
        Assert.Contains(
            mounts,
            mount => mount.ScopeKind == WorldbookScopeKind.Campaign
                     && mount.ScopeId == secondScenario.Id);
        Assert.Contains(mounts, mount => mount.ScopeKind == WorldbookScopeKind.Global);

        await services.WorldbookService.ReplaceScopeMountsAsync(
            imported.Worldbook.Id,
            WorldbookScopeKind.Campaign,
            []);
        mounts = await services.WorldbookService.ListMountsAsync(
            imported.Worldbook.Id);
        Assert.Single(mounts);
        Assert.Equal(WorldbookScopeKind.Global, mounts[0].ScopeKind);
    }

    [Fact]
    public async Task CharacterCardImportCreatesAndReusesItsEmbeddedWorldbook()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();

        var sourcePath = Path.Combine(workspace.Root, "auto-worldbook-card.json");
        await File.WriteAllTextAsync(
            sourcePath,
            """
            {
              "spec": "chara_card_v2",
              "spec_version": "2.0",
              "data": {
                "name": "自动挂载角色",
                "description": "带内置世界书",
                "character_book": {
                  "name": "自动内置世界书",
                  "entries": [
                    {
                      "id": "auto-entry",
                      "name": "自动条目",
                      "keys": ["自动规则"],
                      "content": "自动导入的规则仍然属于该角色卡。",
                      "enabled": true,
                      "constant": false
                    }
                  ]
                }
              }
            }
            """,
            Encoding.UTF8);

        var card = await services.CharacterCards.ImportAsync(sourcePath);
        var books = await services.WorldbookService.ListAsync();
        var book = Assert.Single(books);
        Assert.Equal(WorldbookSourceKind.CharacterCardEmbedded, book.SourceKind);
        Assert.Equal("自动内置世界书", book.Name);
        var mount = Assert.Single(
            await services.WorldbookService.ListMountsAsync(book.Id));
        Assert.Equal(WorldbookScopeKind.Character, mount.ScopeKind);
        Assert.Equal(card.Character.Id, mount.ScopeId);

        var reused = await services.WorldbookService.ImportAsync(
            sourcePath,
            WorldbookScopeKind.Global,
            null);
        Assert.Equal(book.Id, reused.Worldbook.Id);
        Assert.Equal(
            2,
            (await services.WorldbookService.ListMountsAsync(book.Id)).Count);
    }

    [Fact]
    public async Task FailedEmbeddingRebuildKeepsPreviousIndexAndPreviewStaysLocal()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();

        var sourcePath = Path.Combine(workspace.Root, "atomic-worldbook.json");
        await File.WriteAllTextAsync(
            sourcePath,
            """
            {
              "name": "事务世界书",
              "entries": {
                "1": {
                  "uid": 1,
                  "keys": ["事务规则"],
                  "content": "旧索引内容必须在远程 Embedding 失败时继续可用。",
                  "enabled": true,
                  "constant": false,
                  "extensions": {"semanticEnabled": true}
                }
              }
            }
            """,
            Encoding.UTF8);
        var imported = await services.WorldbookService.ImportAsync(
            sourcePath,
            WorldbookScopeKind.Global,
            null);
        var fake = new RecordingEmbeddingGateway();
        await services.ModelAssignments.UpsertAsync(
            new ModelFunctionAssignment
            {
                FunctionKind = ModelFunctionKind.Embedding,
                ProviderId = "builtin-openrouter",
                ModelId = "fixture-embedding-model",
                ContextLimit = 1024,
                MaxOutputTokens = 1,
                Temperature = 0,
                TopP = 1
            });
        var service = new WorldbookService(
            services.Worldbooks,
            services.ModelAssignments,
            fake,
            services.CharacterCardCodecs,
            services.MacroEngine,
            services.Providers);

        await service.RebuildIndexAsync(imported.Worldbook.Id);
        var beforeFailure = await services.Worldbooks.ListChunksAsync(
            new HashSet<string> { imported.Worldbook.Id });
        Assert.Single(beforeFailure);
        Assert.Equal(1, fake.RequestCount);

        await service.RebuildIndexAsync(imported.Worldbook.Id);
        Assert.Equal(1, fake.RequestCount);

        var localPreview = await service.RetrieveAsync(
            new WorldbookRetrievalRequest(
                "atomic-preview",
                null,
                "Embedding",
                new Dictionary<string, string>(),
                AllowRemoteEmbedding: false));
        Assert.NotEmpty(localPreview.Matches);
        Assert.Equal(1, fake.RequestCount);
        Assert.Contains(
            localPreview.Diagnostics,
            item => item.Contains("本地上下文预览", StringComparison.Ordinal));

        var provider = await services.Providers.GetAsync("builtin-openrouter");
        Assert.NotNull(provider);
        provider!.BaseUrl += "/changed-endpoint";
        await services.Providers.UpsertAsync(provider);
        fake.Fail = true;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RebuildIndexAsync(imported.Worldbook.Id));
        var afterFailure = await services.Worldbooks.ListChunksAsync(
            new HashSet<string> { imported.Worldbook.Id });
        Assert.Single(afterFailure);
        Assert.Equal(beforeFailure[0].Content, afterFailure[0].Content);
    }

    private sealed class RecordingEmbeddingGateway : IEmbeddingProviderGateway
    {
        public bool Fail { get; set; }
        public int RequestCount { get; private set; }

        public Task<EmbeddingResponse> CreateEmbeddingsAsync(
            EmbeddingRequest request,
            CancellationToken cancellationToken = default)
        {
            RequestCount++;
            if (Fail)
            {
                throw new InvalidOperationException("fixture embedding failure");
            }

            return Task.FromResult(
                new EmbeddingResponse(
                    request.Inputs
                        .Select((_, index) =>
                            new EmbeddingVector(index, [1f, 0f]))
                        .ToArray()));
        }
    }
}
