using System.Runtime.CompilerServices;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;
using TavernDesk.Infrastructure;

namespace TavernDesk.Tests;

public sealed class ContextAndConcurrencyTests
{
    [Fact]
    public void TokenEstimatorUsesBundledTokenizerForKnownModelAndFallbackForUnknownModel()
    {
        var services = new InfrastructureServices();
        var segment = new ContextSegment(
            "input:test",
            ContextSegmentKind.UserInput,
            "Test input",
            "tiktoken is great!",
            IsPinned: true,
            Order: 0,
            ProviderRole: "user");

        var knownModel = services.TokenEstimator.Estimate(
            [segment],
            32768,
            0,
            "openai/gpt-4o");
        var unknownModel = services.TokenEstimator.Estimate(
            [segment],
            32768,
            0,
            "deepseek/deepseek-chat");

        Assert.Equal(13, knownModel.InputTokens);
        Assert.Equal(10, unknownModel.InputTokens);
        Assert.False(knownModel.IsExact);
    }

    [Fact]
    public async Task ContextAssemblyIncludesCharacterMemoryHistoryAndInput()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var character = new Character
        {
            Name = "上下文角色",
            Description = "角色描述",
            Personality = "沉稳",
            Scenario = "图书馆"
        };
        await services.Characters.UpsertAsync(character);
        var conversation = new Conversation
        {
            CharacterId = character.Id,
            Title = "上下文会话"
        };
        await services.Conversations.UpsertAsync(conversation);
        await services.Conversations.AddMessageAsync(new ChatMessage
        {
            ConversationId = conversation.Id,
            SenderKind = MessageSenderKind.User,
            SenderId = "local-user",
            Content = "历史消息"
        });
        await services.MemoryBanks.SaveBodyAsync(character.Id, "已保存记忆", 5000);

        var result = await services.ContextAssembler.AssembleAsync(
            new ContextAssemblyRequest(
                conversation.Id,
                "当前输入",
                32768,
                4096,
                MemoryOverride: "正在编辑的记忆"));

        Assert.Contains(result.Segments, item => item.Kind == ContextSegmentKind.Character);
        Assert.Contains(result.Segments, item =>
            item.Kind == ContextSegmentKind.Memory
            && item.Content == "正在编辑的记忆");
        Assert.Contains(result.Segments, item =>
            item.Kind == ContextSegmentKind.History
            && item.Content.Contains("历史消息"));
        Assert.Contains(result.Segments, item =>
            item.Kind == ContextSegmentKind.UserInput
            && item.Content == "当前输入");
        Assert.False(result.Estimate.IsExact);
        Assert.True(result.Estimate.InputTokens > 0);
    }

    [Fact]
    public async Task DifferentConversationsCanStreamConcurrentlyWithoutCrossingChunks()
    {
        var coordinator = new TavernDesk.Infrastructure.Context.ConversationGenerationCoordinator();
        var first = new List<string>();
        var second = new List<string>();

        var firstTask = coordinator.RunAsync(
            "conversation-a",
            token => StreamAsync(["A1", "A2", "A3"], token),
            (chunk, _) =>
            {
                first.Add(chunk);
                return ValueTask.CompletedTask;
            });
        var secondTask = coordinator.RunAsync(
            "conversation-b",
            token => StreamAsync(["B1", "B2"], token),
            (chunk, _) =>
            {
                second.Add(chunk);
                return ValueTask.CompletedTask;
            });

        await Task.WhenAll(firstTask, secondTask);

        Assert.Equal(["A1", "A2", "A3"], first);
        Assert.Equal(["B1", "B2"], second);
        Assert.Equal(
            ConversationGenerationStatus.Completed,
            coordinator.GetState("conversation-a").Status);
        Assert.Equal(
            ConversationGenerationStatus.Completed,
            coordinator.GetState("conversation-b").Status);
    }

    [Fact]
    public async Task CancelAllStopsEveryActiveGenerationAndMarksThemInterrupted()
    {
        var coordinator =
            new TavernDesk.Infrastructure.Context.ConversationGenerationCoordinator();
        var firstEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new List<ConversationGenerationState>();
        coordinator.StateChanged += (_, state) =>
        {
            lock (observed)
            {
                observed.Add(state);
            }
        };

        var firstTask = coordinator.RunAsync(
            "chat:a",
            token => HoldUntilCancelledAsync(firstEntered, token),
            (_, _) => ValueTask.CompletedTask);
        var secondTask = coordinator.RunAsync(
            "campaign:b:seat:1",
            token => HoldUntilCancelledAsync(secondEntered, token),
            (_, _) => ValueTask.CompletedTask);
        await Task.WhenAll(firstEntered.Task, secondEntered.Task);

        var cancelledCount = await coordinator.CancelAllAsync();
        await Task.WhenAll(firstTask, secondTask);

        Assert.Equal(2, cancelledCount);
        Assert.Equal(
            ConversationGenerationStatus.Interrupted,
            coordinator.GetState("chat:a").Status);
        Assert.Equal(
            ConversationGenerationStatus.Interrupted,
            coordinator.GetState("campaign:b:seat:1").Status);
        lock (observed)
        {
            Assert.Contains(observed, state =>
                state.ConversationId == "chat:a"
                && state.Status == ConversationGenerationStatus.Stopping);
            Assert.Contains(observed, state =>
                state.ConversationId == "campaign:b:seat:1"
                && state.Status == ConversationGenerationStatus.Stopping);
        }
    }

    [Fact]
    public async Task ContextAssemblyUsesPersonaPresetWorldbookRolesAndHistoryCutoff()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var character = new Character
        {
            Name = "世界书角色",
            RawCardJson = """
                {
                  "spec": "chara_card_v3",
                  "spec_version": "3.0",
                  "data": {
                    "name": "世界书角色",
                    "description": "固定描述",
                    "personality": "",
                    "scenario": "",
                    "first_mes": "",
                    "mes_example": "",
                    "system_prompt": "角色系统规则",
                    "post_history_instructions": "角色后置规则",
                    "character_book": {
                      "entries": [
                        {"keys":[],"content":"常量条目","enabled":true,"constant":true,"insertion_order":20},
                        {"keys":["月亮"],"content":"命中条目","enabled":true,"constant":false,"insertion_order":30},
                        {"keys":["太阳"],"content":"不应命中","enabled":true,"constant":false,"insertion_order":40}
                      ]
                    }
                  }
                }
                """
        };
        await services.Characters.UpsertAsync(character);
        var conversation = new Conversation
        {
            CharacterId = character.Id,
            Title = "结构会话"
        };
        await services.Conversations.UpsertAsync(conversation);
        var first = new ChatMessage
        {
            ConversationId = conversation.Id,
            SenderKind = MessageSenderKind.User,
            Content = "月亮升起来了"
        };
        var second = new ChatMessage
        {
            ConversationId = conversation.Id,
            SenderKind = MessageSenderKind.Character,
            Content = "我看见了"
        };
        await services.Conversations.AddMessageAsync(first);
        await services.Conversations.AddMessageAsync(second);

        var result = await services.ContextAssembler.AssembleAsync(
            new ContextAssemblyRequest(
                conversation.Id,
                "继续",
                32768,
                2048,
                PersonaName: "林",
                PersonaDescription: "旅行者",
                GlobalPreset: "全局预设",
                HistoryBeforeSequenceNo: second.SequenceNo));

        Assert.Contains(result.Segments, segment =>
            segment.Kind == ContextSegmentKind.Preset
            && segment.Content == "全局预设");
        Assert.Contains(result.Segments, segment =>
            segment.Kind == ContextSegmentKind.Persona
            && segment.Content.Contains("旅行者"));
        Assert.Contains(result.Segments, segment =>
            segment.Kind == ContextSegmentKind.Worldbook
            && segment.Content == "常量条目");
        Assert.Contains(result.Segments, segment =>
            segment.Kind == ContextSegmentKind.Worldbook
            && segment.Content == "命中条目");
        Assert.DoesNotContain(result.Segments, segment =>
            segment.Content.Contains("不应命中"));
        var history = Assert.Single(
            result.Segments,
            segment => segment.Kind == ContextSegmentKind.History);
        Assert.Equal("user", history.ProviderRole);
        Assert.Equal(first.Content, history.Content);
        Assert.DoesNotContain(result.Segments, segment =>
            segment.Id == $"message:{second.Id}");
        Assert.Equal(
            "user",
            result.Segments.Single(segment =>
                segment.Kind == ContextSegmentKind.UserInput).ProviderRole);
        Assert.Contains(result.Segments, segment =>
            segment.Kind == ContextSegmentKind.PostHistory);
    }

    private static async IAsyncEnumerable<string> StreamAsync(
        IEnumerable<string> chunks,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var chunk in chunks)
        {
            await Task.Delay(12, cancellationToken);
            yield return chunk;
        }
    }

    private static async IAsyncEnumerable<string> HoldUntilCancelledAsync(
        TaskCompletionSource entered,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        entered.TrySetResult();
        yield return "started";
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }
}
