using TavernDesk.Core.Models;

namespace TavernDesk.Core.Abstractions;

public enum ContextSegmentKind
{
    Safety,
    Preset,
    Character,
    Persona,
    Worldbook,
    Memory,
    Search,
    Knowledge,
    History,
    PostHistory,
    UserInput
}

public sealed record ContextSegment(
    string Id,
    ContextSegmentKind Kind,
    string Title,
    string Content,
    bool IsPinned,
    int Order,
    string ProviderRole = "system",
    string? ProviderContent = null);

public sealed record TokenEstimate(
    int InputTokens,
    int ReservedOutputTokens,
    int ContextLimit,
    bool IsExact)
{
    public int TotalTokens => InputTokens + ReservedOutputTokens;
    public bool ExceedsLimit => TotalTokens > ContextLimit;
}

public sealed record ContextAssemblyRequest(
    string ConversationId,
    string UserInput,
    int ContextLimit,
    int ReservedOutputTokens,
    string? MemoryOverride = null,
    string? PersonaName = null,
    string? PersonaDescription = null,
    string? GlobalPreset = null,
    long? HistoryBeforeSequenceNo = null,
    string? SpeakerCharacterId = null,
    IReadOnlyList<string>? GroupMemberIds = null,
    string? GroupMemoryOverride = null,
    string? GroupMemberMemoryOverride = null,
    bool GroupMemberMemoryEnabled = true,
    string? GroupSystemPrompt = null,
    string? GroupBatonInstruction = null,
    RetrievalContextOptions? Retrieval = null,
    string? ModelId = null,
    string? ContinuationInstruction = null,
    bool AllowRemoteSemanticRetrieval = true);

public sealed record ContextAssemblyResult(
    IReadOnlyList<ContextSegment> Segments,
    TokenEstimate Estimate,
    IReadOnlyList<string>? Diagnostics = null,
    GroupContextBudgetResult? GroupBudget = null);

public interface ITokenEstimator
{
    TokenEstimate Estimate(
        IEnumerable<ContextSegment> segments,
        int contextLimit,
        int reservedOutputTokens,
        string? modelId = null);
}

public interface IContextAssembler
{
    Task<ContextAssemblyResult> AssembleAsync(
        ContextAssemblyRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record ContextBudget(
    int ContextLimit,
    int ReservedOutputTokens,
    string SourceLabel,
    string? ModelId = null);

public interface IContextBudgetProvider
{
    ContextBudget GetCurrentBudget();
    void UpdateBudget(ContextBudget budget);
}

public enum ConversationGenerationStatus
{
    Idle,
    Queued,
    Streaming,
    Stopping,
    Completed,
    Interrupted,
    Failed
}

public sealed record ConversationGenerationState(
    string ConversationId,
    string? GenerationId,
    ConversationGenerationStatus Status,
    string? ErrorMessage,
    DateTimeOffset UpdatedAt,
    int ReceivedTokens = 0);

public interface IConversationGenerationCoordinator
{
    event EventHandler<ConversationGenerationState>? StateChanged;

    ConversationGenerationState GetState(string conversationId);

    void ReportReceivedText(string operationId, string content);

    Task RunAsync(
        string conversationId,
        Func<CancellationToken, IAsyncEnumerable<string>> streamFactory,
        Func<string, CancellationToken, ValueTask> receiveChunk,
        CancellationToken cancellationToken = default);

    Task RunProviderAsync(
        string operationId,
        Func<CancellationToken, IAsyncEnumerable<ProviderStreamEvent>> streamFactory,
        Func<ProviderStreamEvent, CancellationToken, ValueTask> receiveEvent,
        CancellationToken cancellationToken = default);

    bool Cancel(string conversationId);

    Task<int> CancelAllAsync();
}

public enum LiveReplyKind
{
    NewMessage,
    CandidateReplacement
}

public sealed record ConversationGenerationSession(
    string ConversationId,
    string? OperationId,
    bool IsBusy,
    string? MessageId,
    string? SenderId,
    LiveReplyKind ReplyKind,
    string PartialContent,
    bool IsThinking,
    bool SawReasoning,
    bool SawContent,
    ProviderTokenUsage? Usage,
    string? FinishReason,
    DateTimeOffset UpdatedAt);

public interface IConversationGenerationSessionStore
{
    event EventHandler<ConversationGenerationSession>? SessionChanged;

    ConversationGenerationSession Get(string conversationId);

    bool TryBegin(string conversationId, out string operationId);

    CancellationToken GetCancellationToken(
        string conversationId,
        string operationId);

    bool Cancel(string conversationId);

    bool BeginReply(
        string conversationId,
        string operationId,
        string messageId,
        string senderId,
        LiveReplyKind replyKind);

    bool ApplyProviderEvent(
        string conversationId,
        string operationId,
        ProviderStreamEvent streamEvent);

    bool End(string conversationId, string operationId);
    void Forget(string conversationId);
}

public interface IMemoryBankService
{
    Task<MemoryBank?> GetAsync(string ownerId, CancellationToken cancellationToken = default);
    Task<string?> GetBodyAsync(string ownerId, CancellationToken cancellationToken = default);
    Task SaveBodyAsync(string ownerId, string body, int targetTokens, CancellationToken cancellationToken = default);
    Task<bool> TrySaveBodyAsync(
        string ownerId,
        string body,
        int targetTokens,
        long? expectedRevision,
        CancellationToken cancellationToken = default);
}

public interface IChatTool
{
    string Name { get; }
    string Description { get; }
    Task<string> InvokeAsync(string argumentsJson, CancellationToken cancellationToken = default);
}
