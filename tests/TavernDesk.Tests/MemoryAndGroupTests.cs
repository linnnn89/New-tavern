using System.Runtime.CompilerServices;
using System.Text.Json;
using TavernDesk.App.Services;
using TavernDesk.App.ViewModels;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;
using TavernDesk.Infrastructure;

namespace TavernDesk.Tests;

public sealed class MemoryAndGroupTests
{
    [Fact]
    public async Task MemoryDraftCommitUpdatesBankAndCheckpointAtomically()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var character = new Character { Name = "记忆角色" };
        await services.Characters.UpsertAsync(character);
        var conversation = new Conversation
        {
            CharacterId = character.Id,
            Title = "记忆会话"
        };
        await services.Conversations.UpsertAsync(conversation);
        await services.MemoryBanks.SaveBodyAsync(character.Id, "旧记忆", 5000);
        var user = new ChatMessage
        {
            ConversationId = conversation.Id,
            SenderKind = MessageSenderKind.User,
            SenderId = "local-user",
            Content = "我答应明天回来"
        };
        var reply = new ChatMessage
        {
            ConversationId = conversation.Id,
            SenderKind = MessageSenderKind.Character,
            SenderId = character.Id,
            Content = "我会等你"
        };
        await services.Conversations.AddMessageAsync(user);
        await services.Conversations.AddMessageAsync(reply);

        var settings = await services.MemoryWorkflow.GetSettingsAsync(character.Id);
        var plan = services.MemoryPrompts.BuildUpdate(
            character.Id,
            conversation.Id,
            "旧记忆",
            5000,
            settings,
            checkpoint: null,
            await services.Conversations.ListMessagesAsync(conversation.Id),
            new Dictionary<string, string> { [character.Id] = character.Name });
        var draft = new MemoryUpdateDraft
        {
            TargetOwnerId = character.Id,
            SourceConversationId = conversation.Id,
            Kind = MemoryDraftKind.Update,
            Body = "新记忆草稿",
            RequestPreview = "fixture-preview",
            TargetTokens = plan.TargetTokens,
            SourceThroughSequenceNo = plan.SourceThroughSequenceNo,
            SourceUserTurns = plan.SourceUserTurns
        };
        await services.MemoryWorkflow.SaveDraftAsync(draft);
        await services.MemoryWorkflow.CommitDraftAsync(
            draft.Id,
            "用户编辑后的新记忆",
            5000);

        var bank = Assert.IsType<MemoryBank>(
            await services.MemoryBanks.GetAsync(character.Id));
        var checkpoint = Assert.IsType<MemoryCheckpoint>(
            await services.MemoryWorkflow.GetCheckpointAsync(
                character.Id,
                conversation.Id));
        Assert.Equal("用户编辑后的新记忆", bank.Body);
        Assert.Equal(reply.SequenceNo, checkpoint.LastSequenceNo);
        Assert.Equal(1, checkpoint.ProcessedUserTurns);
        Assert.Null(await services.MemoryWorkflow.GetDraftAsync(
            character.Id,
            conversation.Id,
            MemoryDraftKind.Update));

        var compression = new MemoryUpdateDraft
        {
            TargetOwnerId = character.Id,
            SourceConversationId = conversation.Id,
            Kind = MemoryDraftKind.Compression,
            Body = "压缩草稿",
            RequestPreview = "fixture-compression",
            TargetTokens = 3000,
            SourceThroughSequenceNo = checkpoint.LastSequenceNo
        };
        await services.MemoryWorkflow.SaveDraftAsync(compression);
        await services.MemoryWorkflow.CommitDraftAsync(
            compression.Id,
            "压缩后的记忆",
            3000);
        var unchangedCheckpoint = Assert.IsType<MemoryCheckpoint>(
            await services.MemoryWorkflow.GetCheckpointAsync(
                character.Id,
                conversation.Id));
        Assert.Equal(checkpoint.LastSequenceNo, unchangedCheckpoint.LastSequenceNo);
        Assert.Equal(checkpoint.ProcessedUserTurns, unchangedCheckpoint.ProcessedUserTurns);
    }

    [Fact]
    public async Task GroupMemoryMergeKeepsGroupBankIndependentAndRoleMemoryPrimary()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var first = new Character { Name = "角色甲", Description = "谨慎" };
        var second = new Character { Name = "角色乙", Description = "果断" };
        await services.Characters.UpsertAsync(first);
        await services.Characters.UpsertAsync(second);
        var conversation = new Conversation
        {
            Title = "测试群聊",
            Mode = ConversationMode.Group
        };
        var settings = new GroupChatSettings
        {
            ConversationId = conversation.Id
        };
        await services.GroupChats.CreateAsync(
            conversation,
            settings,
            [
                new GroupChatMember
                {
                    ConversationId = conversation.Id,
                    CharacterId = first.Id,
                    SortIndex = 0
                },
                new GroupChatMember
                {
                    ConversationId = conversation.Id,
                    CharacterId = second.Id,
                    SortIndex = 1
                }
            ]);
        var groupOwner = MemoryOwnerIds.ForGroup(conversation.Id);
        await services.MemoryBanks.SaveBodyAsync(first.Id, "角色本体记忆", 5000);
        await services.MemoryBanks.SaveBodyAsync(groupOwner, "群聊辅助记忆", 5000);

        var plan = services.MemoryPrompts.BuildGroupMerge(
            first.Id,
            first.Name,
            conversation.Id,
            "角色本体记忆",
            "群聊辅助记忆",
            5000,
            settings);
        Assert.True(
            plan.InputPayload.IndexOf("角色本体记忆", StringComparison.Ordinal)
            < plan.InputPayload.IndexOf("群聊辅助记忆", StringComparison.Ordinal));
        var draft = new MemoryUpdateDraft
        {
            TargetOwnerId = first.Id,
            SourceConversationId = conversation.Id,
            Kind = MemoryDraftKind.GroupMerge,
            Body = "合并草稿",
            RequestPreview = "fixture-merge",
            TargetTokens = plan.TargetTokens
        };
        await services.MemoryWorkflow.SaveDraftAsync(draft);
        await services.MemoryWorkflow.CommitDraftAsync(
            draft.Id,
            "合并后的角色记忆",
            5000);

        Assert.Equal(
            "合并后的角色记忆",
            (await services.MemoryBanks.GetAsync(first.Id))?.Body);
        Assert.Equal(
            "群聊辅助记忆",
            (await services.MemoryBanks.GetAsync(groupOwner))?.Body);
        Assert.Equal(2, (await services.GroupChats.ListMembersAsync(conversation.Id)).Count);
        settings.RelayMode = GroupRelayMode.FixedOrder;
        settings.AutoContinueEnabled = true;
        settings.MaximumAutomaticTurns = 8;
        await services.GroupChats.SaveSettingsAsync(settings);
        var persistedSettings = Assert.IsType<GroupChatSettings>(
            await services.GroupChats.GetSettingsAsync(conversation.Id));
        Assert.Equal(GroupRelayMode.FixedOrder, persistedSettings.RelayMode);
        Assert.True(persistedSettings.AutoContinueEnabled);
        Assert.Equal(8, persistedSettings.MaximumAutomaticTurns);

        var branchPoint = new ChatMessage
        {
            ConversationId = conversation.Id,
            SenderKind = MessageSenderKind.Character,
            SenderId = first.Id,
            Content = "建立独立群聊分支"
        };
        await services.Conversations.AddMessageAsync(branchPoint);
        var fork = await services.Conversations.ForkThroughMessageAsync(
            conversation.Id,
            branchPoint.Id);
        Assert.Equal(ConversationMode.Group, fork.Mode);
        Assert.Equal(2, (await services.GroupChats.ListMembersAsync(fork.Id)).Count);
        Assert.NotNull(await services.GroupChats.GetSettingsAsync(fork.Id));
        Assert.Null(await services.MemoryBanks.GetAsync(MemoryOwnerIds.ForGroup(fork.Id)));
    }

    [Fact]
    public async Task MemoryGenerationUsesOneGlobalPromptAndFixedInputPayload()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();

        var globalPrompts = services.GlobalPrompts.Snapshot()
            .ToDictionary(item => item.Key, item => item.Value);
        globalPrompts[GlobalPromptKey.MemoryUpdateSystem] = "GLOBAL_UPDATE_SYSTEM";
        globalPrompts[GlobalPromptKey.MemoryCompressionSystem] =
            "GLOBAL_COMPRESSION_SYSTEM";
        globalPrompts[GlobalPromptKey.GroupMemoryMergeSystem] =
            "GLOBAL_GROUP_MERGE_SYSTEM";
        await services.GlobalPrompts.SaveAsync(globalPrompts);

        var character = new Character { Name = "全局模板测试角色" };
        await services.Characters.UpsertAsync(character);
        var conversation = new Conversation
        {
            CharacterId = character.Id,
            Title = "全局模板测试会话"
        };
        await services.Conversations.UpsertAsync(conversation);
        await services.Conversations.AddMessageAsync(new ChatMessage
        {
            ConversationId = conversation.Id,
            SenderKind = MessageSenderKind.User,
            SenderId = "local-user",
            Content = "需要写入记忆的新事实"
        });
        await services.MemoryBanks.SaveBodyAsync(
            character.Id,
            "已有角色记忆",
            5000);
        await services.MemoryWorkflow.SaveSettingsAsync(
            new MemoryWorkflowSettings
            {
                OwnerId = character.Id,
                UpdateSystemPrompt = "LEGACY_LOCAL_UPDATE_SYSTEM",
                UpdateUserTemplate = "LEGACY_LOCAL_UPDATE_USER",
                CompressionSystemPrompt = "LEGACY_LOCAL_COMPRESSION_SYSTEM",
                CompressionUserTemplate = "LEGACY_LOCAL_COMPRESSION_USER"
            });

        await services.Providers.UpsertAsync(new ProviderProfile
        {
            Id = "fixture-provider",
            Name = "Fixture Provider",
            BaseUrl = "https://fixture.invalid/v1"
        });
        await services.Models.ReplaceAsync(
            "fixture-provider",
            [new ProviderModelDescriptor("fixture-model", "Fixture Model")]);
        foreach (var functionKind in new[]
                 {
                     ModelFunctionKind.MemoryUpdate,
                     ModelFunctionKind.MemoryCompression,
                     ModelFunctionKind.GroupMemoryMerge
                 })
        {
            await services.ModelAssignments.UpsertAsync(
                new ModelFunctionAssignment
                {
                    FunctionKind = functionKind,
                    ProviderId = "fixture-provider",
                    ModelId = "fixture-model"
                });
        }

        var gateway = new RecordingMemoryGateway();
        var viewModel = new MemoryWorkflowViewModel(
            services.MemoryBanks,
            services.MemoryWorkflow,
            services.MemoryPrompts,
            services.Conversations,
            services.Characters,
            services.ModelAssignments,
            gateway,
            services.GenerationCoordinator,
            services.GlobalPrompts);
        await viewModel.LoadAsync(
            character.Id,
            conversation.Id,
            character.Name);

        viewModel.GenerateUpdateCommand.Execute(null);
        await WaitUntilAsync(() => Task.FromResult(
            gateway.Requests.Count >= 1 && !viewModel.IsGenerating));
        viewModel.GenerateCompressionCommand.Execute(null);
        await WaitUntilAsync(() => Task.FromResult(
            gateway.Requests.Count >= 2 && !viewModel.IsGenerating));

        var groupConversation = new Conversation
        {
            Title = "全局群聊记忆模板测试",
            Mode = ConversationMode.Group
        };
        await services.Conversations.UpsertAsync(groupConversation);
        var groupOwnerId = MemoryOwnerIds.ForGroup(groupConversation.Id);
        await services.MemoryBanks.SaveBodyAsync(
            groupOwnerId,
            "已有群聊记忆",
            5000);
        await viewModel.LoadAsync(
            groupOwnerId,
            groupConversation.Id,
            groupConversation.Title);
        await viewModel.GenerateGroupMergeAsync(
            character,
            new GroupChatSettings
            {
                ConversationId = groupConversation.Id,
                MergeSystemPrompt = "LEGACY_LOCAL_GROUP_MERGE_SYSTEM",
                MergeUserTemplate = "LEGACY_LOCAL_GROUP_MERGE_USER"
            });

        Assert.Collection(
            gateway.Requests,
            request =>
            {
                Assert.Contains(
                    request.Messages,
                    message => message.Role == "system"
                               && message.Content == "GLOBAL_UPDATE_SYSTEM");
                Assert.Contains(
                    request.Messages,
                    message => message.Role == "user"
                               && message.Content.Contains(
                                   "已有角色记忆",
                                   StringComparison.Ordinal)
                               && message.Content.Contains(
                                   "需要写入记忆的新事实",
                                   StringComparison.Ordinal));
            },
            request =>
            {
                Assert.Contains(
                    request.Messages,
                    message => message.Role == "system"
                               && message.Content == "GLOBAL_COMPRESSION_SYSTEM");
                Assert.Contains(
                    request.Messages,
                    message => message.Role == "user"
                               && message.Content.Contains(
                                   "已有角色记忆",
                                   StringComparison.Ordinal));
            },
            request =>
            {
                Assert.Contains(
                    request.Messages,
                    message => message.Role == "system"
                               && message.Content == "GLOBAL_GROUP_MERGE_SYSTEM");
                Assert.Contains(
                    request.Messages,
                    message => message.Role == "user"
                               && message.Content.Contains(
                                   "已有角色记忆",
                                   StringComparison.Ordinal)
                               && message.Content.Contains(
                                   "已有群聊记忆",
                                   StringComparison.Ordinal));
            });
        Assert.DoesNotContain(
            gateway.Requests.SelectMany(request => request.Messages),
            message => message.Content.Contains(
                "LEGACY_LOCAL_",
                StringComparison.Ordinal));
    }

    [Fact]
    public void MentionRelayUsesOnlyLastSentenceAndPausesForUser()
    {
        var conversationId = "group-fixture";
        var members = new[]
        {
            new GroupChatMember
            {
                ConversationId = conversationId,
                CharacterId = "a",
                SortIndex = 0
            },
            new GroupChatMember
            {
                ConversationId = conversationId,
                CharacterId = "b",
                SortIndex = 1
            }
        };
        var names = new Dictionary<string, string>
        {
            ["a"] = "角色甲",
            ["b"] = "角色乙"
        };
        var settings = new GroupChatSettings
        {
            ConversationId = conversationId,
            RelayMode = GroupRelayMode.MentionDirected
        };
        var planner = new TavernDesk.Infrastructure.Group.GroupRelayPlanner();
        var directed = planner.DecideNext(
            settings,
            members,
            names,
            [
                new ChatMessage
                {
                    ConversationId = conversationId,
                    SequenceNo = 1,
                    SenderKind = MessageSenderKind.Character,
                    SenderId = "a",
                    Content = "轮到你了，@角色乙"
                }
            ],
            "旅行者");
        Assert.Equal("b", directed.NextSpeakerId);
        Assert.False(directed.PauseForUser);

        var earlierOnly = planner.DecideNext(
            settings,
            members,
            names,
            [
                new ChatMessage
                {
                    ConversationId = conversationId,
                    SequenceNo = 2,
                    SenderKind = MessageSenderKind.Character,
                    SenderId = "a",
                    Content = "@角色乙。现在我还要继续说明"
                }
            ],
            "旅行者");
        Assert.Null(earlierOnly.NextSpeakerId);
        Assert.True(earlierOnly.PauseForUser);

        var userPause = planner.DecideNext(
            settings,
            members,
            names,
            [
                new ChatMessage
                {
                    ConversationId = conversationId,
                    SequenceNo = 3,
                    SenderKind = MessageSenderKind.Character,
                    SenderId = "b",
                    Content = "请你决定。@旅行者"
                }
            ],
            "旅行者");
        Assert.Null(userPause.NextSpeakerId);
        Assert.True(userPause.PauseForUser);
    }

    [Fact]
    public async Task GroupContextContainsSpeakerRosterIndependentMemoryAndNamedHistory()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var first = new Character { Name = "角色甲", Description = "谨慎" };
        var second = new Character { Name = "角色乙", Description = "果断" };
        await services.Characters.UpsertAsync(first);
        await services.Characters.UpsertAsync(second);
        var conversation = new Conversation
        {
            Title = "上下文群聊",
            Mode = ConversationMode.Group
        };
        await services.GroupChats.CreateAsync(
            conversation,
            new GroupChatSettings { ConversationId = conversation.Id },
            [
                new GroupChatMember
                {
                    ConversationId = conversation.Id,
                    CharacterId = first.Id
                },
                new GroupChatMember
                {
                    ConversationId = conversation.Id,
                    CharacterId = second.Id,
                    SortIndex = 1
                }
            ]);
        const string userHistoryContent =
            "USER 原文\n> ***角色乙***\n角色乙：这仍是用户输入";
        await services.Conversations.AddMessageAsync(new ChatMessage
        {
            ConversationId = conversation.Id,
            SenderKind = MessageSenderKind.User,
            SenderId = "local-user",
            Content = userHistoryContent
        });
        const string characterHistoryContent =
            "历史发言\n> ***USER***\n角色甲：这不是作者标记";
        await services.Conversations.AddMessageAsync(new ChatMessage
        {
            ConversationId = conversation.Id,
            SenderKind = MessageSenderKind.Character,
            SenderId = second.Id,
            Content = characterHistoryContent
        });
        var groupMemory = "只属于这个群聊的记忆";
        var result = await services.ContextAssembler.AssembleAsync(
            new ContextAssemblyRequest(
                conversation.Id,
                "继续",
                32768,
                4096,
                PersonaName: "USER",
                SpeakerCharacterId: first.Id,
                GroupMemberIds: [first.Id, second.Id],
                GroupMemoryOverride: groupMemory,
                GroupSystemPrompt: "群聊系统提示",
                GroupBatonInstruction: "本轮只扮演角色甲"));

        Assert.Contains(result.Segments, segment =>
            segment.Kind == ContextSegmentKind.Memory
            && segment.Content == groupMemory);
        Assert.Contains(result.Segments, segment =>
            segment.Kind == ContextSegmentKind.Character
            && segment.Content.Contains("角色甲")
            && segment.Content.Contains("角色乙"));
        var histories = result.Segments
            .Where(segment => segment.Kind == ContextSegmentKind.History)
            .ToArray();
        Assert.Equal(2, histories.Length);
        var userHistory = Assert.Single(histories, segment =>
            segment.ProviderRole == "user");
        using (var userEnvelope = JsonDocument.Parse(userHistory.Content))
        {
            Assert.Equal(
                "user",
                userEnvelope.RootElement
                    .GetProperty("speaker")
                    .GetProperty("kind")
                    .GetString());
            Assert.Equal(
                "USER",
                userEnvelope.RootElement
                    .GetProperty("speaker")
                    .GetProperty("name")
                    .GetString());
            Assert.Equal(
                userHistoryContent,
                userEnvelope.RootElement
                    .GetProperty("content")
                    .GetString());
        }

        var history = Assert.Single(histories, segment =>
            segment.Kind == ContextSegmentKind.History
            && segment.ProviderRole == "assistant");
        using (var envelope = JsonDocument.Parse(history.Content))
        {
            Assert.Equal(
                "character",
                envelope.RootElement
                    .GetProperty("speaker")
                    .GetProperty("kind")
                    .GetString());
            Assert.Equal(
                "角色乙",
                envelope.RootElement
                    .GetProperty("speaker")
                    .GetProperty("name")
                    .GetString());
            Assert.Equal(
                characterHistoryContent,
                envelope.RootElement
                    .GetProperty("content")
                    .GetString());
        }
        Assert.Equal(history.Content, history.ProviderContent);
        Assert.Contains(result.Segments, segment =>
            segment.Kind == ContextSegmentKind.PostHistory
            && segment.Content == "本轮只扮演角色甲");
    }

    [Fact]
    public async Task GroupChatAutomaticallyRelaysThenPausesOnUserMention()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var first = new Character { Name = "角色甲" };
        var second = new Character { Name = "角色乙" };
        await services.Characters.UpsertAsync(first);
        await services.Characters.UpsertAsync(second);
        var conversation = new Conversation
        {
            Title = "自动接力群聊",
            Mode = ConversationMode.Group
        };
        await services.GroupChats.CreateAsync(
            conversation,
            new GroupChatSettings
            {
                ConversationId = conversation.Id,
                RelayMode = GroupRelayMode.MentionDirected,
                AutoContinueEnabled = true,
                MaximumAutomaticTurns = 4
            },
            [
                new GroupChatMember
                {
                    ConversationId = conversation.Id,
                    CharacterId = first.Id,
                    SortIndex = 0
                },
                new GroupChatMember
                {
                    ConversationId = conversation.Id,
                    CharacterId = second.Id,
                    SortIndex = 1
                }
            ]);
        await services.Providers.UpsertAsync(new ProviderProfile
        {
            Id = "group-fixture-provider",
            Name = "Group Fixture",
            BaseUrl = "https://fixture.invalid/v1"
        });
        await services.Models.ReplaceAsync(
            "group-fixture-provider",
            [new ProviderModelDescriptor("group-fixture-model", "Group Fixture Model")]);
        await services.ModelAssignments.UpsertAsync(new ModelFunctionAssignment
        {
            FunctionKind = ModelFunctionKind.GroupChat,
            ProviderId = "group-fixture-provider",
            ModelId = "group-fixture-model",
            ContextLimit = 32768,
            MaxOutputTokens = 1024
        });
        var viewModel = new ChatViewModel(
            services.Conversations,
            services.Characters,
            services.MemoryBanks,
            services.MemoryWorkflow,
            services.MemoryPrompts,
            services.GroupChats,
            services.GroupRelay,
            services.Retrieval,
            services.Presets,
            services.PresetResolver,
            services.ContextAssembler,
            services.ContextBudget,
            services.GenerationCoordinator,
            services.GenerationSessions,
            services.ModelAssignments,
            new GroupRelayGateway(),
            services.Settings,
            services.GlobalPrompts,
            new NoOpInteractionService(),
            services.ChatArchives,
            new NoOpFileDialogService());
        await viewModel.LoadAsync();
        var item = viewModel.ConversationGroups
            .SelectMany(group => group.AllConversations)
            .Single(candidate => candidate.Id == conversation.Id);
        viewModel.SelectConversationCommand.Execute(item);
        await Task.Delay(150);
        viewModel.ComposerText = "大家开始吧";
        await Task.Delay(220);
        Assert.True(viewModel.SendLocalCommand.CanExecute(null));
        viewModel.SendLocalCommand.Execute(null);
        await WaitUntilAsync(async () =>
            (await services.GroupChats.GetStateAsync(conversation.Id)).IsPaused
            && !viewModel.IsCurrentConversationBusy);

        var messages = await services.Conversations.ListMessagesAsync(conversation.Id);
        Assert.Equal(
            ["大家开始吧", "甲发言。@角色乙", "乙发言。@USER"],
            messages.Select(message => message.Content));
        Assert.Equal([first.Id, second.Id], messages
            .Where(message => message.SenderKind == MessageSenderKind.Character)
            .Select(message => message.SenderId));
    }

    private static async Task WaitUntilAsync(
        Func<Task<bool>> condition,
        int timeoutMilliseconds = 5000)
    {
        using var timeout = new CancellationTokenSource(timeoutMilliseconds);
        while (!await condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private sealed class GroupRelayGateway : IProviderGateway
    {
        public Task<IReadOnlyList<ProviderModelDescriptor>> RefreshModelsAsync(
            string providerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProviderModelDescriptor>>([]);

        public async IAsyncEnumerable<ProviderStreamEvent> StreamChatAsync(
            ModelExecutionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var baton = request.Messages
                .Last(message => message.Content.Contains("本轮只以“"));
            await Task.Delay(40, cancellationToken);
            yield return new ProviderStreamEvent(
                ProviderStreamEventKind.Content,
                baton.Content.Contains("本轮只以“角色甲”")
                    ? "甲发言。@角色乙"
                    : "乙发言。@USER");
            yield return new ProviderStreamEvent(
                ProviderStreamEventKind.Completed,
                FinishReason: "stop");
        }
    }

    private sealed class RecordingMemoryGateway : IProviderGateway
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
                "测试记忆草稿");
            yield return new ProviderStreamEvent(
                ProviderStreamEventKind.Completed,
                Usage: new ProviderTokenUsage(12, 6, 18),
                FinishReason: "stop");
        }
    }

    private sealed class NoOpInteractionService : IUserInteractionService
    {
        public Task<string?> EditTextAsync(
            string title,
            string prompt,
            string initialText) =>
            Task.FromResult<string?>(null);

        public DeleteMessageDecision ConfirmMessageDeletion() =>
            DeleteMessageDecision.Cancel;

        public UnsavedChangesDecision ConfirmUnsavedCharacterChanges(string characterName) =>
            UnsavedChangesDecision.Cancel;

        public UnsavedChangesDecision ConfirmUnsavedProviderChanges(string providerName) =>
            UnsavedChangesDecision.Cancel;

        public bool ConfirmCharacterDeletion(string characterName, int conversationCount) => false;
        public bool ConfirmShelfDeletion(string shelfName) => false;
        public bool ConfirmPresetDeletion(string presetName) => false;
        public bool ConfirmProviderDeletion(string providerName) => false;
        public bool ConfirmSecretClear(string providerName) => false;
        public Task<GroupChatDraft?> CreateGroupChatAsync(
            IReadOnlyList<Character> characters) =>
            Task.FromResult<GroupChatDraft?>(null);
        public void CopyText(string text)
        {
        }
    }
}
