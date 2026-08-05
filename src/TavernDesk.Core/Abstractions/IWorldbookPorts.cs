using TavernDesk.Core.Models;

namespace TavernDesk.Core.Abstractions;

public interface IWorldbookRepository
{
    Task<IReadOnlyList<Worldbook>> ListAsync(
        CancellationToken cancellationToken = default);

    Task<Worldbook?> GetAsync(
        string worldbookId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Worldbook>> ListEnabledForCharacterAsync(
        string? characterId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorldbookMount>> ListMountsAsync(
        string worldbookId,
        CancellationToken cancellationToken = default);

    Task UpsertMountAsync(
        WorldbookMount mount,
        CancellationToken cancellationToken = default);

    Task RemoveMountAsync(
        string worldbookId,
        WorldbookScopeKind scopeKind,
        string scopeId,
        CancellationToken cancellationToken = default);

    Task ReplaceCharacterMountsAsync(
        string worldbookId,
        IReadOnlyList<WorldbookMount> mounts,
        CancellationToken cancellationToken = default);

    Task ReplaceScopeMountsAsync(
        string worldbookId,
        WorldbookScopeKind scopeKind,
        IReadOnlyList<WorldbookMount> mounts,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorldbookEntry>> ListEntriesAsync(
        string worldbookId,
        CancellationToken cancellationToken = default);

    Task UpdateEntryTitleAsync(
        string worldbookId,
        string entryId,
        string title,
        CancellationToken cancellationToken = default);

    Task UpsertAsync(
        Worldbook worldbook,
        IReadOnlyList<WorldbookEntry> entries,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        string worldbookId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorldbookChunk>> ListChunksAsync(
        IReadOnlySet<string> worldbookIds,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorldbookTextHit>> SearchTextAsync(
        IReadOnlySet<string> worldbookIds,
        string queryText,
        int maximumResults,
        CancellationToken cancellationToken = default);

    Task ReplaceChunksAsync(
        string worldbookId,
        IReadOnlyList<WorldbookChunk> chunks,
        CancellationToken cancellationToken = default);

    Task ReplaceIndexedChunksAsync(
        string worldbookId,
        IReadOnlyList<WorldbookChunk> chunks,
        EmbeddingProfile profile,
        IReadOnlyList<WorldbookEmbedding> embeddings,
        CancellationToken cancellationToken = default);

    Task UpsertEmbeddingProfileAsync(
        EmbeddingProfile profile,
        CancellationToken cancellationToken = default);

    Task<EmbeddingProfile?> GetEmbeddingProfileAsync(
        string profileId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorldbookEmbedding>> ListEmbeddingsAsync(
        IReadOnlySet<string> chunkIds,
        string profileId,
        CancellationToken cancellationToken = default);

    Task ReplaceEmbeddingsAsync(
        string profileId,
        IReadOnlyList<WorldbookEmbedding> embeddings,
        CancellationToken cancellationToken = default);
}

public sealed record WorldbookTextHit(
    string ChunkId,
    string WorldbookId,
    string EntryId,
    double Rank);

public sealed record WorldbookImportResult(
    Worldbook Worldbook,
    IReadOnlyList<WorldbookEntry> Entries,
    IReadOnlyList<string> Warnings);

public sealed record WorldbookIndexResult(
    string WorldbookId,
    int ChunkCount,
    int EmbeddingDimension,
    IReadOnlyList<string> Diagnostics);

public sealed record WorldbookRetrievalRequest(
    string ConversationId,
    string? CharacterId,
    string QueryText,
    IReadOnlyDictionary<string, string> MacroVariables,
    int MaximumResults = 6,
    int TokenBudget = 1200,
    double MinimumScore = 0.30,
    bool AllowRemoteEmbedding = true);

public sealed record WorldbookRetrievalResult(
    IReadOnlyList<WorldbookMatch> Matches,
    IReadOnlyList<string> Diagnostics);

public interface IWorldbookService
{
    Task<IReadOnlyList<Worldbook>> ListAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorldbookEntry>> ListEntriesAsync(
        string worldbookId,
        CancellationToken cancellationToken = default);

    Task UpdateEntryTitleAsync(
        string worldbookId,
        string entryId,
        string title,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorldbookMount>> ListMountsAsync(
        string worldbookId,
        CancellationToken cancellationToken = default);

    Task UpsertMountAsync(
        WorldbookMount mount,
        CancellationToken cancellationToken = default);

    Task RemoveMountAsync(
        string worldbookId,
        WorldbookScopeKind scopeKind,
        string scopeId,
        CancellationToken cancellationToken = default);

    Task ReplaceCharacterMountsAsync(
        string worldbookId,
        IReadOnlyList<WorldbookMount> mounts,
        CancellationToken cancellationToken = default);

    Task ReplaceScopeMountsAsync(
        string worldbookId,
        WorldbookScopeKind scopeKind,
        IReadOnlyList<WorldbookMount> mounts,
        CancellationToken cancellationToken = default);

    Task<WorldbookImportResult> ImportAsync(
        string sourcePath,
        WorldbookScopeKind scopeKind,
        string? scopeId,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        string worldbookId,
        CancellationToken cancellationToken = default);

    Task<WorldbookIndexResult> RebuildIndexAsync(
        string worldbookId,
        CancellationToken cancellationToken = default);

    Task<WorldbookRetrievalResult> RetrieveAsync(
        WorldbookRetrievalRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Worldbook>> ListEnabledForCharacterAsync(
        string? characterId,
        CancellationToken cancellationToken = default);
}
