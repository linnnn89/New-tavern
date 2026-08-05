using TavernDesk.App.Services;
using TavernDesk.App.ViewModels;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;
using TavernDesk.Infrastructure;

namespace TavernDesk.Tests;

public sealed class ChatSelectionIsolationTests
{
    [Fact]
    public async Task SlowerPreviousSelectionCannotOverwriteCurrentConversation()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var firstCharacter = new Character { Name = "角色 A" };
        var secondCharacter = new Character { Name = "角色 B" };
        await services.Characters.UpsertAsync(firstCharacter);
        await services.Characters.UpsertAsync(secondCharacter);

        var firstConversation = new Conversation
        {
            CharacterId = firstCharacter.Id,
            Title = "A 会话"
        };
        var secondConversation = new Conversation
        {
            CharacterId = secondCharacter.Id,
            Title = "B 会话"
        };
        await services.Conversations.UpsertAsync(firstConversation);
        await services.Conversations.UpsertAsync(secondConversation);
        await services.Conversations.AddMessageAsync(new ChatMessage
        {
            ConversationId = firstConversation.Id,
            SenderKind = MessageSenderKind.Character,
            SenderId = firstCharacter.Id,
            Content = "A 的消息"
        });
        await services.Conversations.AddMessageAsync(new ChatMessage
        {
            ConversationId = secondConversation.Id,
            SenderKind = MessageSenderKind.Character,
            SenderId = secondCharacter.Id,
            Content = "B 的消息"
        });

        var delayedRepository = new DelayedConversationRepository(
            services.Conversations,
            firstConversation.Id,
            TimeSpan.FromMilliseconds(180));
        var viewModel = new ChatViewModel(
            delayedRepository,
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
            services.ProviderGateway,
            services.Settings,
            services.GlobalPrompts,
            new NoOpInteractionService(),
            services.ChatArchives,
            new NoOpFileDialogService());
        await viewModel.LoadAsync();

        var firstItem = viewModel.ConversationGroups
            .SelectMany(group => group.AllConversations)
            .Single(item => item.Id == firstConversation.Id);
        var secondItem = viewModel.ConversationGroups
            .SelectMany(group => group.AllConversations)
            .Single(item => item.Id == secondConversation.Id);

        viewModel.SelectConversationCommand.Execute(firstItem);
        await Task.Delay(20);
        viewModel.SelectConversationCommand.Execute(secondItem);
        await Task.Delay(260);

        Assert.Equal(secondConversation.Id, viewModel.SelectedConversation?.Id);
        var visible = Assert.Single(viewModel.Messages);
        Assert.Equal("B 的消息", visible.Content);
    }

    private sealed class NoOpInteractionService : IUserInteractionService
    {
        public Task<string?> EditTextAsync(string title, string prompt, string initialText) =>
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

    private sealed class DelayedConversationRepository : IConversationRepository
    {
        private readonly IConversationRepository _inner;
        private readonly string _delayedConversationId;
        private readonly TimeSpan _delay;

        public DelayedConversationRepository(
            IConversationRepository inner,
            string delayedConversationId,
            TimeSpan delay)
        {
            _inner = inner;
            _delayedConversationId = delayedConversationId;
            _delay = delay;
        }

        public Task<IReadOnlyList<ConversationSummary>> ListRecentAsync(
            int limit,
            CancellationToken cancellationToken = default) =>
            _inner.ListRecentAsync(limit, cancellationToken);

        public Task<IReadOnlyList<ConversationSummary>> ListAllAsync(
            CancellationToken cancellationToken = default) =>
            _inner.ListAllAsync(cancellationToken);

        public Task<IReadOnlyList<ConversationSummary>> ListByCharacterAsync(
            string characterId,
            CancellationToken cancellationToken = default) =>
            _inner.ListByCharacterAsync(characterId, cancellationToken);

        public Task<ConversationSummary?> GetLatestForCharacterAsync(
            string characterId,
            CancellationToken cancellationToken = default) =>
            _inner.GetLatestForCharacterAsync(characterId, cancellationToken);

        public Task<Conversation?> GetAsync(
            string id,
            CancellationToken cancellationToken = default) =>
            _inner.GetAsync(id, cancellationToken);

        public async Task<IReadOnlyList<ChatMessage>> ListMessagesAsync(
            string conversationId,
            CancellationToken cancellationToken = default)
        {
            if (conversationId == _delayedConversationId)
            {
                await Task.Delay(_delay, cancellationToken);
            }

            return await _inner.ListMessagesAsync(conversationId, cancellationToken);
        }

        public Task UpsertAsync(
            Conversation conversation,
            CancellationToken cancellationToken = default) =>
            _inner.UpsertAsync(conversation, cancellationToken);

        public Task AddMessageAsync(
            ChatMessage message,
            CancellationToken cancellationToken = default) =>
            _inner.AddMessageAsync(message, cancellationToken);

        public Task AddCandidateAsync(
            MessageCandidate candidate,
            CancellationToken cancellationToken = default) =>
            _inner.AddCandidateAsync(candidate, cancellationToken);

        public Task<IReadOnlyList<MessageCandidate>> ListCandidatesAsync(
            string messageId,
            CancellationToken cancellationToken = default) =>
            _inner.ListCandidatesAsync(messageId, cancellationToken);

        public Task AddAndActivateCandidateAsync(
            MessageCandidate candidate,
            CancellationToken cancellationToken = default) =>
            _inner.AddAndActivateCandidateAsync(candidate, cancellationToken);

        public Task ActivateCandidateAsync(
            string messageId,
            int candidateIndex,
            CancellationToken cancellationToken = default) =>
            _inner.ActivateCandidateAsync(
                messageId,
                candidateIndex,
                cancellationToken);

        public Task UpdateMessageContentAsync(
            string messageId,
            string content,
            CancellationToken cancellationToken = default) =>
            _inner.UpdateMessageContentAsync(messageId, content, cancellationToken);

        public Task DeleteMessageAsync(
            string messageId,
            bool includeSubsequent,
            CancellationToken cancellationToken = default) =>
            _inner.DeleteMessageAsync(messageId, includeSubsequent, cancellationToken);

        public Task<Conversation> ForkThroughMessageAsync(
            string conversationId,
            string messageId,
            CancellationToken cancellationToken = default) =>
            _inner.ForkThroughMessageAsync(conversationId, messageId, cancellationToken);

        public Task<int> CountAsync(CancellationToken cancellationToken = default) =>
            _inner.CountAsync(cancellationToken);
    }
}
