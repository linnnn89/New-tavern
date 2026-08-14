using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;
using TavernDesk.Infrastructure;

var root = Path.Combine(
    Path.GetTempPath(),
    "TavernDesk-GroupContextHistoryTdd",
    Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var services = new InfrastructureServices(root);
    await services.InitializeAsync();
    var characters = Enumerable.Range(1, 3)
        .Select(index => new Character
        {
            Name = $"测试角色{index}",
            Description = $"离线测试角色 {index}。"
        })
        .ToArray();
    foreach (var character in characters)
    {
        await services.Characters.UpsertAsync(character);
    }

    var conversation = new Conversation
    {
        Title = "离线长上下文阶段测试",
        Mode = ConversationMode.Group
    };
    await services.GroupChats.CreateAsync(
        conversation,
        new GroupChatSettings { ConversationId = conversation.Id },
        characters.Select((character, index) => new GroupChatMember
        {
            ConversationId = conversation.Id,
            CharacterId = character.Id,
            SortIndex = index
        }).ToArray());

    var stageMessages = new Dictionary<int, List<string>>();
    for (var stage = 1; stage <= 8; stage++)
    {
        stageMessages[stage] = [];
        var participants = new (MessageSenderKind Kind, string SenderId)[]
        {
            (MessageSenderKind.User, "local-user"),
            (MessageSenderKind.Character, characters[0].Id),
            (MessageSenderKind.Character, characters[1].Id),
            (MessageSenderKind.Character, characters[2].Id)
        };
        for (var messageIndex = 0; messageIndex < participants.Length; messageIndex++)
        {
            var marker = $"[[STAGE:{stage}|MESSAGE:{messageIndex + 1}]]";
            var message = new ChatMessage
            {
                ConversationId = conversation.Id,
                SenderKind = participants[messageIndex].Kind,
                SenderId = participants[messageIndex].SenderId,
                Content = marker + " 规范的多角色剧情内容。" + new string('叙', 1_500)
            };
            await services.Conversations.AddMessageAsync(message);
            stageMessages[stage].Add(message.Id);
        }
    }

    var result = await services.ContextAssembler.AssembleAsync(
        new ContextAssemblyRequest(
            conversation.Id,
            "继续当前场景",
            ContextLimit: 8_000,
            ReservedOutputTokens: 1_000,
            SpeakerCharacterId: characters[0].Id,
            GroupMemberIds: characters.Select(character => character.Id).ToArray(),
            GroupSystemPrompt: "保持角色一致，并接续完整阶段。",
            GroupBatonInstruction: "由当前角色继续发言。",
            AllowRemoteSemanticRetrieval: false));

    var budget = result.GroupBudget
        ?? throw new InvalidOperationException("没有生成群聊预算结果。");
    if (!budget.CanSend)
    {
        throw new InvalidOperationException(
            $"离线测试请求不可发送：{budget.FailureReason}");
    }

    var selectedIds = result.Segments
        .Where(segment => segment.Kind == ContextSegmentKind.History)
        .Select(segment => segment.Id["message:".Length..])
        .ToHashSet(StringComparer.Ordinal);
    var selectedStages = stageMessages
        .Where(pair => pair.Value.Any(selectedIds.Contains))
        .Select(pair => pair.Key)
        .ToArray();
    if (selectedStages.Length == 0 || selectedStages.Length >= stageMessages.Count)
    {
        throw new InvalidOperationException(
            $"测试未形成有效截断：保留 {selectedStages.Length}/{stageMessages.Count} 个阶段。");
    }

    var expectedSuffix = Enumerable.Range(
            stageMessages.Count - selectedStages.Length + 1,
            selectedStages.Length)
        .ToArray();
    if (!selectedStages.SequenceEqual(expectedSuffix))
    {
        throw new InvalidOperationException(
            $"历史不是连续后缀：实际 {string.Join(',', selectedStages)}；预期 {string.Join(',', expectedSuffix)}。");
    }

    foreach (var stage in selectedStages)
    {
        var actualCount = stageMessages[stage].Count(selectedIds.Contains);
        if (actualCount != stageMessages[stage].Count)
        {
            throw new InvalidOperationException(
                $"阶段 {stage} 被截断：仅保留 {actualCount}/{stageMessages[stage].Count} 条消息。");
        }
    }

    var retrievalConversation = new Conversation
    {
        Title = "离线完整阶段召回测试",
        Mode = ConversationMode.Group
    };
    await services.GroupChats.CreateAsync(
        retrievalConversation,
        new GroupChatSettings { ConversationId = retrievalConversation.Id },
        characters.Select((character, index) => new GroupChatMember
        {
            ConversationId = retrievalConversation.Id,
            CharacterId = character.Id,
            SortIndex = index
        }).ToArray());
    var openingMessages = new[]
    {
        new ChatMessage
        {
            ConversationId = retrievalConversation.Id,
            SenderKind = MessageSenderKind.Character,
            SenderId = characters[0].Id,
            Content = "OPENING_SIGNAL AI_A"
        },
        new ChatMessage
        {
            ConversationId = retrievalConversation.Id,
            SenderKind = MessageSenderKind.Character,
            SenderId = characters[1].Id,
            Content = "OPENING_SIGNAL AI_B"
        },
        new ChatMessage
        {
            ConversationId = retrievalConversation.Id,
            SenderKind = MessageSenderKind.Tool,
            SenderId = "offline-tool",
            Content = "OPENING_SIGNAL TOOL_RESULT"
        }
    };
    var targetMessages = new[]
    {
        new ChatMessage
        {
            ConversationId = retrievalConversation.Id,
            SenderKind = MessageSenderKind.User,
            SenderId = "local-user",
            Content = "LANTERN_RECALL TARGET_USER"
        },
        new ChatMessage
        {
            ConversationId = retrievalConversation.Id,
            SenderKind = MessageSenderKind.Character,
            SenderId = characters[0].Id,
            Content = "LANTERN_RECALL TARGET_AI"
        }
    };
    var denseMessages = new List<ChatMessage>
    {
        new()
        {
            ConversationId = retrievalConversation.Id,
            SenderKind = MessageSenderKind.User,
            SenderId = "local-user",
            Content = "LANTERN_RECALL DENSE_USER"
        }
    };
    denseMessages.AddRange(Enumerable.Range(0, 12).Select(index =>
        new ChatMessage
        {
            ConversationId = retrievalConversation.Id,
            SenderKind = MessageSenderKind.Character,
            SenderId = characters[index % characters.Length].Id,
            Content = $"LANTERN_RECALL DENSE_AI_{index}"
        }));
    var recentMessages = new[]
    {
        new ChatMessage
        {
            ConversationId = retrievalConversation.Id,
            SenderKind = MessageSenderKind.User,
            SenderId = "local-user",
            Content = "RECENT_USER"
        },
        new ChatMessage
        {
            ConversationId = retrievalConversation.Id,
            SenderKind = MessageSenderKind.Character,
            SenderId = characters[0].Id,
            Content = "RECENT_AI"
        }
    };
    foreach (var message in openingMessages
                 .Concat(targetMessages)
                 .Concat(denseMessages)
                 .Concat(recentMessages))
    {
        await services.Conversations.AddMessageAsync(message);
    }

    var retrievalOptions = new RetrievalContextOptions(
        true,
        RetrievalScope.CurrentConversation,
        2,
        2,
        20_000,
        new HashSet<string>());
    var retrievalResult = await services.ContextAssembler.AssembleAsync(
        new ContextAssemblyRequest(
            retrievalConversation.Id,
            "LANTERN_RECALL",
            32_768,
            4_096,
            SpeakerCharacterId: characters[0].Id,
            GroupMemberIds: characters.Select(character => character.Id).ToArray(),
            Retrieval: retrievalOptions,
            AllowRemoteSemanticRetrieval: false));
    var recalledStages = retrievalResult.Segments
        .Where(segment => segment.Kind == ContextSegmentKind.Search)
        .ToArray();
    if (recalledStages.Length != 2
        || !recalledStages.Any(segment =>
            segment.Content.Contains("DENSE_USER", StringComparison.Ordinal)
            && segment.Content.Contains("DENSE_AI_11", StringComparison.Ordinal))
        || !recalledStages.Any(segment =>
            segment.Content.Contains("TARGET_USER", StringComparison.Ordinal)
            && segment.Content.Contains("TARGET_AI", StringComparison.Ordinal)))
    {
        throw new InvalidOperationException("密集阶段占满首批候选后，没有继续召回下一完整阶段。");
    }

    var excludedResult = await services.ContextAssembler.AssembleAsync(
        new ContextAssemblyRequest(
            retrievalConversation.Id,
            "LANTERN_RECALL",
            32_768,
            4_096,
            SpeakerCharacterId: characters[0].Id,
            GroupMemberIds: characters.Select(character => character.Id).ToArray(),
            Retrieval: retrievalOptions with
            {
                ExcludedMessageIds = new HashSet<string> { denseMessages[3].Id }
            },
            AllowRemoteSemanticRetrieval: false));
    var replacement = excludedResult.Segments
        .Single(segment => segment.Kind == ContextSegmentKind.Search);
    if (!replacement.Content.Contains("TARGET_USER", StringComparison.Ordinal)
        || replacement.Content.Contains("DENSE_USER", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("排除密集阶段后没有补入下一完整阶段。");
    }

    var openingResult = await services.ContextAssembler.AssembleAsync(
        new ContextAssemblyRequest(
            retrievalConversation.Id,
            "OPENING_SIGNAL",
            32_768,
            4_096,
            SpeakerCharacterId: characters[0].Id,
            GroupMemberIds: characters.Select(character => character.Id).ToArray(),
            Retrieval: retrievalOptions with { MaximumResults = 1 },
            AllowRemoteSemanticRetrieval: false));
    var opening = openingResult.Segments
        .Single(segment => segment.Kind == ContextSegmentKind.Search);
    if (!opening.Content.Contains("AI_A", StringComparison.Ordinal)
        || !opening.Content.Contains("AI_B", StringComparison.Ordinal)
        || !opening.Content.Contains("TOOL_RESULT", StringComparison.Ordinal)
        || !opening.Content.Contains(
            "（历史发言者：Tool）",
            StringComparison.Ordinal)
        || opening.Content.Contains(
            "（历史发言者：未知）",
            StringComparison.Ordinal))
    {
        throw new InvalidOperationException("开场 AI/工具消息没有作为完整阶段召回。");
    }

    Console.WriteLine(
        $"PASS stages={string.Join(',', selectedStages)} actual={budget.ActualInputTokens} available={budget.AvailableInputTokens} retrieval=complete api=disabled");
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    if (Directory.Exists(root))
    {
        Directory.Delete(root, recursive: true);
    }
}
