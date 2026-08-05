using TavernDesk.Core.Models;

namespace TavernDesk.Core.Abstractions;

public sealed record ProviderModelDescriptor(
    string ModelId,
    string DisplayName,
    int? ContextLimit = null,
    int? MaxOutputTokens = null,
    bool SupportsStreaming = true,
    ModelCatalogKind ModelKind = ModelCatalogKind.Chat);

public sealed record ProviderChatMessage(
    string Role,
    string Content);

public sealed record ModelExecutionRequest(
    string ProviderId,
    string ModelId,
    IReadOnlyList<ProviderChatMessage> Messages,
    int MaxOutputTokens,
    double Temperature,
    double TopP,
    bool? ReasoningEnabled = null,
    string? SessionId = null);

public sealed record EmbeddingRequest(
    string ProviderId,
    string ModelId,
    IReadOnlyList<string> Inputs);

public sealed record EmbeddingVector(
    int Index,
    IReadOnlyList<float> Values);

public sealed record EmbeddingResponse(
    IReadOnlyList<EmbeddingVector> Vectors,
    ProviderTokenUsage? Usage = null);

public enum ProviderStreamEventKind
{
    Reasoning,
    Content,
    Completed
}

public sealed record ProviderTokenUsage(
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens,
    int? ReasoningTokens = null,
    int? CachedPromptTokens = null,
    int? UncachedPromptTokens = null);

public sealed record ProviderStreamEvent(
    ProviderStreamEventKind Kind,
    string Content = "",
    ProviderTokenUsage? Usage = null,
    string? FinishReason = null);

public interface ISecretStore
{
    Task<string> SaveAsync(
        string ownerId,
        string secret,
        CancellationToken cancellationToken = default);

    Task<string?> ReadAsync(
        string reference,
        CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(
        string reference,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        string reference,
        CancellationToken cancellationToken = default);
}

public interface IModelCatalogRepository
{
    Task<IReadOnlyList<ProviderModel>> ListAsync(
        string providerId,
        ModelCatalogKind? modelKind = null,
        CancellationToken cancellationToken = default);

    Task ReplaceAsync(
        string providerId,
        IReadOnlyList<ProviderModelDescriptor> models,
        ModelCatalogKind modelKind = ModelCatalogKind.Chat,
        CancellationToken cancellationToken = default);

    Task UpsertAsync(
        ProviderModel model,
        CancellationToken cancellationToken = default);
}

public interface IModelAssignmentRepository
{
    Task<IReadOnlyList<ModelFunctionAssignment>> ListAsync(
        CancellationToken cancellationToken = default);

    Task<ModelFunctionAssignment?> GetAsync(
        ModelFunctionKind functionKind,
        CancellationToken cancellationToken = default);

    Task UpsertAsync(
        ModelFunctionAssignment assignment,
        CancellationToken cancellationToken = default);
}

public interface IProviderGateway
{
    Task<IReadOnlyList<ProviderModelDescriptor>> RefreshModelsAsync(
        string providerId,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<ProviderStreamEvent> StreamChatAsync(
        ModelExecutionRequest request,
        CancellationToken cancellationToken = default);
}

public interface IEmbeddingModelCatalogGateway
{
    Task<IReadOnlyList<ProviderModelDescriptor>> RefreshEmbeddingModelsAsync(
        string providerId,
        CancellationToken cancellationToken = default);
}

public interface IEmbeddingProviderGateway
{
    Task<EmbeddingResponse> CreateEmbeddingsAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken = default);
}
