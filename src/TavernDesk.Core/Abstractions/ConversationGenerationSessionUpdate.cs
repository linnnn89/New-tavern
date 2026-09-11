namespace TavernDesk.Core.Abstractions;

/// <summary>
/// An immutable event version whose full text is materialized only if a reader
/// needs it. Display queues can discard intermediate versions without copying text.
/// </summary>
public sealed class ConversationGenerationSessionUpdate
{
    private readonly ConversationGenerationSession _metadata;
    private readonly Lazy<ConversationGenerationSession> _snapshot;

    public ConversationGenerationSessionUpdate(
        ConversationGenerationSession metadata, Func<string>? readContent = null)
    {
        _metadata = metadata;
        _snapshot = new Lazy<ConversationGenerationSession>(() => readContent is null
            ? metadata : metadata with { PartialContent = readContent() });
    }

    public string ConversationId => _metadata.ConversationId;
    public string? OperationId => _metadata.OperationId;
    public string? MessageId => _metadata.MessageId;
    public bool IsBusy => _metadata.IsBusy;
    public bool IsThinking => _metadata.IsThinking;
    public bool SawContent => _metadata.SawContent;
    public bool HasCompletion => _metadata.FinishReason is not null || _metadata.Usage is not null;

    public ConversationGenerationSession GetSnapshot() => _snapshot.Value;
}

/// <summary>
/// Optional display subscription. The original store contract and eager
/// SessionChanged event remain available to existing consumers.
/// </summary>
public interface IConversationGenerationSessionUpdates
{
    event EventHandler<ConversationGenerationSessionUpdate>? SessionUpdated;
}
