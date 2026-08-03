using System.Text.Json.Nodes;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;
using TavernDesk.Infrastructure;
using TavernDesk.Infrastructure.Compatibility;
using TavernDesk.Infrastructure.Context;

namespace TavernDesk.Tests;

public sealed class CompatibilityAndRetrievalTests
{
    [Fact]
    public void SafeMacrosAreDeterministicAndLeaveUnknownMacrosUntouched()
    {
        var engine = new SafeMacroEngine();
        var variables = new Dictionary<string, string>
        {
            ["char"] = "雪乃",
            ["__seed"] = "fixed-seed"
        };
        const string template =
            "{{char}}/{{pick::甲::乙::丙}}/{{roll::2d6+1}}/{{unknown}}";

        var first = engine.Expand(template, variables);
        var second = engine.Expand(template, variables);

        Assert.Equal(first, second);
        Assert.StartsWith("雪乃/", first);
        Assert.EndsWith("/{{unknown}}", first);
    }

    [Fact]
    public async Task WorldbookSupportsConstantSelectiveAndRecursiveActivation()
    {
        var engine = new CharacterWorldbookEngine(new SafeMacroEngine());
        var card = """
            {
              "spec": "chara_card_v3",
              "data": {
                "character_book": {
                  "scan_depth": 4,
                  "entries": [
                    {
                      "id": "constant",
                      "keys": [],
                      "content": "{{char}}的固定规则",
                      "constant": true,
                      "enabled": true,
                      "insertion_order": 10
                    },
                    {
                      "id": "primary",
                      "keys": ["月亮"],
                      "secondary_keys": ["图书馆"],
                      "selective_logic": "and_all",
                      "content": "星辰线索",
                      "enabled": true,
                      "insertion_order": 20
                    },
                    {
                      "id": "recursive",
                      "keys": ["星辰线索"],
                      "content": "递归命中的规则",
                      "enabled": true,
                      "insertion_order": 30
                    }
                  ]
                }
              }
            }
            """;

        var result = await engine.ScanAsync(new WorldbookScanRequest(
            "conversation-1",
            card,
            [],
            "月亮照进图书馆",
            new Dictionary<string, string>
            {
                ["char"] = "雪乃",
                ["__seed"] = "worldbook"
            }));

        Assert.Equal(
            ["constant", "primary", "recursive"],
            result.Matches.Select(match => match.Id));
        Assert.Equal("雪乃的固定规则", result.Matches[0].Content);
        Assert.Equal(1, result.Matches[2].RecursionLevel);
    }

    [Fact]
    public async Task PresetsMergeGlobalCharacterConversationAndUnionKnownArrays()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var character = new Character { Name = "预设角色" };
        await services.Characters.UpsertAsync(character);
        var conversation = new Conversation
        {
            CharacterId = character.Id,
            Title = "预设会话"
        };
        await services.Conversations.UpsertAsync(conversation);

        var globalBase = new PromptPreset
        {
            Name = "全局基础",
            OverlayJson = """
                {
                  "prompt": {
                    "systemPrompt": "全局基础",
                    "injectionGroups": ["base"]
                  },
                  "temperature": 0.1
                }
                """
        };
        var globalLate = new PromptPreset
        {
            Name = "全局后置",
            OverlayJson = """
                {
                  "prompt": {
                    "systemPrompt": {"on": true, "value": "全局后置"},
                    "injectionGroups": ["late"]
                  },
                  "temperature": {"on": false, "value": 0.9}
                }
                """
        };
        var characterPreset = new PromptPreset
        {
            Name = "角色覆盖",
            OverlayJson = """
                {
                  "prompt": {
                    "systemPrompt": "角色覆盖",
                    "injectionGroups": ["character"]
                  }
                }
                """
        };
        var conversationPreset = new PromptPreset
        {
            Name = "会话覆盖",
            OverlayJson = """
                {
                  "prompt": {
                    "systemPrompt": "会话覆盖",
                    "injectionGroups": ["conversation"]
                  }
                }
                """
        };
        foreach (var preset in new[]
                 {
                     globalBase,
                     globalLate,
                     characterPreset,
                     conversationPreset
                 })
        {
            await services.Presets.UpsertAsync(preset);
        }

        await services.Presets.SetMountAsync(new PresetMount(
            PresetScopeKind.Global,
            "global",
            globalBase.Id,
            0,
            true));
        await services.Presets.SetMountAsync(new PresetMount(
            PresetScopeKind.Global,
            "global",
            globalLate.Id,
            10,
            true));
        await services.Presets.SetMountAsync(new PresetMount(
            PresetScopeKind.Character,
            character.Id,
            characterPreset.Id,
            0,
            true));
        await services.Presets.SetMountAsync(new PresetMount(
            PresetScopeKind.Conversation,
            conversation.Id,
            conversationPreset.Id,
            0,
            true));

        var resolved = await services.PresetResolver.ResolveAsync(
            character.Id,
            conversation.Id);
        var overlay = JsonNode.Parse(resolved.OverlayJson)!.AsObject();

        Assert.Equal("会话覆盖", resolved.SystemPrompt);
        Assert.Equal(0.1, overlay["temperature"]!.GetValue<double>());
        Assert.Equal(
            ["base", "late", "character", "conversation"],
            overlay["prompt"]!["injectionGroups"]!.AsArray()
                .Select(node => node!.GetValue<string>()));
        Assert.Equal(4, resolved.Diagnostics.Count);
    }

    [Fact]
    public async Task TrigramRetrievalTracksEditsDeletesScopesAndExclusions()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var character = new Character { Name = "召回角色" };
        await services.Characters.UpsertAsync(character);
        var current = new Conversation
        {
            CharacterId = character.Id,
            Title = "当前会话"
        };
        var sibling = new Conversation
        {
            CharacterId = character.Id,
            Title = "同角色旧会话"
        };
        await services.Conversations.UpsertAsync(current);
        await services.Conversations.UpsertAsync(sibling);
        var currentMessage = new ChatMessage
        {
            ConversationId = current.Id,
            SenderKind = MessageSenderKind.Character,
            SenderId = character.Id,
            Content = "雪乃记得图书馆约定"
        };
        var siblingMessage = new ChatMessage
        {
            ConversationId = sibling.Id,
            SenderKind = MessageSenderKind.Character,
            SenderId = character.Id,
            Content = "雪乃在图书馆留下书签"
        };
        await services.Conversations.AddMessageAsync(currentMessage);
        await services.Conversations.AddMessageAsync(siblingMessage);

        var currentMatches = await services.Retrieval.SearchAsync(
            Query(
                current,
                character,
                RetrievalScope.CurrentConversation,
                "图书馆约定"));
        Assert.Equal(currentMessage.Id, Assert.Single(currentMatches).MessageId);

        var siblingMatches = await services.Retrieval.SearchAsync(
            Query(
                current,
                character,
                RetrievalScope.SameCharacter,
                "留下书签"));
        Assert.Equal(siblingMessage.Id, Assert.Single(siblingMatches).MessageId);

        var excluded = await services.Retrieval.SearchAsync(
            Query(
                current,
                character,
                RetrievalScope.SameCharacter,
                "留下书签",
                new HashSet<string> { siblingMessage.Id }));
        Assert.Empty(excluded);

        await services.Conversations.UpdateMessageContentAsync(
            currentMessage.Id,
            "雪乃改为海边约定");
        Assert.Empty(await services.Retrieval.SearchAsync(
            Query(
                current,
                character,
                RetrievalScope.CurrentConversation,
                "图书馆约定")));
        Assert.Equal(
            currentMessage.Id,
            Assert.Single(await services.Retrieval.SearchAsync(
                Query(
                    current,
                    character,
                    RetrievalScope.CurrentConversation,
                    "海边约定"))).MessageId);

        await services.Conversations.DeleteMessageAsync(
            currentMessage.Id,
            includeSubsequent: false);
        Assert.Empty(await services.Retrieval.SearchAsync(
            Query(
                current,
                character,
                RetrievalScope.CurrentConversation,
                "海边约定")));
    }

    [Fact]
    public async Task ContextKeepsRecentHistoryAndInjectsInspectableRetrieval()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var character = new Character { Name = "长记忆角色" };
        await services.Characters.UpsertAsync(character);
        var conversation = new Conversation
        {
            CharacterId = character.Id,
            Title = "长上下文会话"
        };
        await services.Conversations.UpsertAsync(conversation);
        var messages = new[]
        {
            "图书馆月光约定需要长期记住",
            "第二条无关内容",
            "第三条无关内容",
            "第四条近期内容",
            "第五条近期内容"
        }.Select((content, index) => new ChatMessage
        {
            ConversationId = conversation.Id,
            SenderKind = index % 2 == 0
                ? MessageSenderKind.User
                : MessageSenderKind.Character,
            SenderId = index % 2 == 0 ? "local-user" : character.Id,
            Content = content
        }).ToArray();
        foreach (var message in messages)
        {
            await services.Conversations.AddMessageAsync(message);
        }

        var result = await services.ContextAssembler.AssembleAsync(
            new ContextAssemblyRequest(
                conversation.Id,
                "请回想图书馆月光约定",
                32768,
                4096,
                Retrieval: new RetrievalContextOptions(
                    true,
                    RetrievalScope.CurrentConversation,
                    2,
                    4,
                    1000,
                    new HashSet<string>())));

        Assert.Equal(
            2,
            result.Segments.Count(segment =>
                segment.Kind == ContextSegmentKind.History));
        var retrieved = Assert.Single(
            result.Segments,
            segment => segment.Kind == ContextSegmentKind.Search);
        Assert.Equal($"retrieval:{messages[0].Id}", retrieved.Id);
        Assert.Contains(
            Assert.IsAssignableFrom<IReadOnlyList<string>>(result.Diagnostics),
            diagnostic => diagnostic.Contains("实际注入 1 条"));

        var excluded = await services.ContextAssembler.AssembleAsync(
            new ContextAssemblyRequest(
                conversation.Id,
                "请回想图书馆月光约定",
                32768,
                4096,
                Retrieval: new RetrievalContextOptions(
                    true,
                    RetrievalScope.CurrentConversation,
                    2,
                    4,
                    1000,
                    new HashSet<string> { messages[0].Id })));
        Assert.DoesNotContain(
            excluded.Segments,
            segment => segment.Kind == ContextSegmentKind.Search);
    }

    private static MessageRetrievalQuery Query(
        Conversation conversation,
        Character character,
        RetrievalScope scope,
        string text,
        IReadOnlySet<string>? excluded = null) =>
        new(
            conversation.Id,
            character.Id,
            scope,
            text,
            null,
            10,
            excluded ?? new HashSet<string>());
}
