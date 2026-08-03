using TavernDesk.Core.Models;

namespace TavernDesk.Core.Abstractions;

public interface IDatabaseInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

public interface ICharacterRepository
{
    Task<IReadOnlyList<Character>> ListAsync(CancellationToken cancellationToken = default);
    Task<Character?> GetAsync(string id, CancellationToken cancellationToken = default);
    Task UpsertAsync(Character character, CancellationToken cancellationToken = default);
    Task DeleteAsync(string id, CancellationToken cancellationToken = default);
    Task<int> CountAsync(CancellationToken cancellationToken = default);
}

public interface ICharacterShelfRepository
{
    Task<IReadOnlyList<CharacterShelf>> ListAsync(
        CancellationToken cancellationToken = default);
    Task UpsertAsync(
        CharacterShelf shelf,
        CancellationToken cancellationToken = default);
    Task DeleteAsync(
        string shelfId,
        CancellationToken cancellationToken = default);
    Task<IReadOnlySet<string>> ListCharacterIdsAsync(
        string shelfId,
        CancellationToken cancellationToken = default);
    Task AddCharacterAsync(
        string shelfId,
        string characterId,
        CancellationToken cancellationToken = default);
    Task RemoveCharacterAsync(
        string shelfId,
        string characterId,
        CancellationToken cancellationToken = default);
}

public interface IConversationRepository
{
    Task<IReadOnlyList<ConversationSummary>> ListRecentAsync(
        int limit,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ConversationSummary>> ListAllAsync(
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ConversationSummary>> ListByCharacterAsync(
        string characterId,
        CancellationToken cancellationToken = default);
    Task<ConversationSummary?> GetLatestForCharacterAsync(
        string characterId,
        CancellationToken cancellationToken = default);

    Task<Conversation?> GetAsync(string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ChatMessage>> ListMessagesAsync(
        string conversationId,
        CancellationToken cancellationToken = default);

    Task UpsertAsync(Conversation conversation, CancellationToken cancellationToken = default);
    Task AddMessageAsync(ChatMessage message, CancellationToken cancellationToken = default);
    Task AddCandidateAsync(
        MessageCandidate candidate,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MessageCandidate>> ListCandidatesAsync(
        string messageId,
        CancellationToken cancellationToken = default);
    Task AddAndActivateCandidateAsync(
        MessageCandidate candidate,
        CancellationToken cancellationToken = default);
    Task UpdateMessageContentAsync(
        string messageId,
        string content,
        CancellationToken cancellationToken = default);
    Task DeleteMessageAsync(
        string messageId,
        bool includeSubsequent,
        CancellationToken cancellationToken = default);
    Task<Conversation> ForkThroughMessageAsync(
        string conversationId,
        string messageId,
        CancellationToken cancellationToken = default);
    Task<int> CountAsync(CancellationToken cancellationToken = default);
}

public interface IProviderProfileRepository
{
    Task<IReadOnlyList<ProviderProfile>> ListAsync(CancellationToken cancellationToken = default);
    Task<ProviderProfile?> GetAsync(
        string id,
        CancellationToken cancellationToken = default);
    Task UpsertAsync(ProviderProfile profile, CancellationToken cancellationToken = default);
    Task DeleteAsync(
        string id,
        CancellationToken cancellationToken = default);
    Task EnsureDefaultsAsync(CancellationToken cancellationToken = default);
    Task<int> CountEnabledAsync(CancellationToken cancellationToken = default);
}

public interface IAppSettingsRepository
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);
    Task SetAsync(string key, string value, CancellationToken cancellationToken = default);
}
