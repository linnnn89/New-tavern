using System.Collections.Concurrent;
using System.Text;
using TavernDesk.Core.Abstractions;

namespace TavernDesk.Infrastructure.Context;

/// <summary>
/// Holds the live, non-persistent presentation state of each conversation.
/// It is application-scoped so views may detach and later reattach without
/// owning or interrupting the underlying generation operation.
/// </summary>
public sealed class ConversationGenerationSessionStore
    : IConversationGenerationSessionStore, IConversationGenerationSessionUpdates
{
    private readonly ConcurrentDictionary<string, SessionEntry> _active = new();
    private readonly ConcurrentDictionary<string, ConversationGenerationSessionUpdate>
        _lastUpdates = new();

    public event EventHandler<ConversationGenerationSession>? SessionChanged;
    public event EventHandler<ConversationGenerationSessionUpdate>? SessionUpdated;

    public ConversationGenerationSession Get(string conversationId)
    {
        if (_active.TryGetValue(conversationId, out var active))
        {
            return active.Update().GetSnapshot();
        }

        return _lastUpdates.GetOrAdd(
            conversationId,
            static id => new ConversationGenerationSessionUpdate(Empty(id))).GetSnapshot();
    }

    public bool TryBegin(string conversationId, out string operationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        operationId = Guid.NewGuid().ToString("N");
        var entry = new SessionEntry(conversationId, operationId);
        if (!_active.TryAdd(conversationId, entry))
        {
            operationId = string.Empty;
            return false;
        }

        Publish(entry.Update());
        return true;
    }

    public CancellationToken GetCancellationToken(
        string conversationId,
        string operationId) =>
        TryResolve(conversationId, operationId, out var entry)
            ? entry.CancellationToken
            : new CancellationToken(canceled: true);

    public bool Cancel(string conversationId)
    {
        if (!_active.TryGetValue(conversationId, out var entry))
        {
            return false;
        }

        entry.Cancel();
        return true;
    }

    public bool BeginReply(
        string conversationId,
        string operationId,
        string messageId,
        string senderId,
        LiveReplyKind replyKind)
    {
        if (!TryResolve(conversationId, operationId, out var entry))
        {
            return false;
        }

        Publish(entry.BeginReply(messageId, senderId, replyKind));
        return true;
    }

    public bool ApplyProviderEvent(
        string conversationId,
        string operationId,
        ProviderStreamEvent streamEvent)
    {
        if (!TryResolve(conversationId, operationId, out var entry))
        {
            return false;
        }

        Publish(entry.Apply(streamEvent));
        return true;
    }

    public bool End(string conversationId, string operationId)
    {
        if (!TryResolve(conversationId, operationId, out var entry))
        {
            return false;
        }

        var completed = entry.End();
        if (!_active.TryRemove(conversationId, out var removed)
            || !ReferenceEquals(removed, entry))
        {
            return false;
        }

        Publish(completed);
        return true;
    }

    public void Forget(string conversationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        if (_active.ContainsKey(conversationId))
        {
            return;
        }

        _lastUpdates.TryRemove(conversationId, out _);
    }

    private bool TryResolve(
        string conversationId,
        string operationId,
        out SessionEntry entry) =>
        _active.TryGetValue(conversationId, out entry!)
        && string.Equals(
            entry.OperationId,
            operationId,
            StringComparison.Ordinal);

    private void Publish(ConversationGenerationSessionUpdate update)
    {
        // Keep the terminal version ready for reattachment and release its buffer.
        if (!update.IsBusy) update.GetSnapshot();
        _lastUpdates[update.ConversationId] = update;
        SessionUpdated?.Invoke(this, update);
        SessionChanged?.Invoke(this, update.GetSnapshot());
    }

    private static ConversationGenerationSession Empty(string conversationId) =>
        new(
            conversationId,
            OperationId: null,
            IsBusy: false,
            MessageId: null,
            SenderId: null,
            LiveReplyKind.NewMessage,
            PartialContent: string.Empty,
            IsThinking: false,
            SawReasoning: false,
            SawContent: false,
            Usage: null,
            FinishReason: null,
            DateTimeOffset.Now);

    private sealed class SessionEntry
    {
        private readonly object _sync = new();
        private StringBuilder _partialContent = new();
        private ConversationGenerationSessionUpdate? _currentUpdate;
        private string? _messageId;
        private string? _senderId;
        private LiveReplyKind _replyKind;
        private bool _isBusy = true;
        private bool _isThinking;
        private bool _sawReasoning;
        private bool _sawContent;
        private ProviderTokenUsage? _usage;
        private string? _finishReason;
        private DateTimeOffset _updatedAt = DateTimeOffset.Now;
        private readonly CancellationTokenSource _cancellation = new();

        public SessionEntry(string conversationId, string operationId)
        {
            ConversationId = conversationId;
            OperationId = operationId;
        }

        public string ConversationId { get; }
        public string OperationId { get; }
        public CancellationToken CancellationToken => _cancellation.Token;

        public void Cancel() => _cancellation.Cancel();

        public ConversationGenerationSessionUpdate BeginReply(
            string messageId,
            string senderId,
            LiveReplyKind replyKind)
        {
            lock (_sync)
            {
                _messageId = messageId;
                _senderId = senderId;
                _replyKind = replyKind;
                // Older queued versions retain their own append-only buffer.
                _partialContent = new StringBuilder();
                _isThinking = false;
                _sawReasoning = false;
                _sawContent = false;
                _usage = null;
                _finishReason = null;
                _updatedAt = DateTimeOffset.Now;
                return CreateUpdateUnsafe();
            }
        }

        public ConversationGenerationSessionUpdate Apply(
            ProviderStreamEvent streamEvent)
        {
            lock (_sync)
            {
                switch (streamEvent.Kind)
                {
                    case ProviderStreamEventKind.Reasoning:
                        _sawReasoning = true;
                        _isThinking = !_sawContent;
                        break;

                    case ProviderStreamEventKind.Content
                        when streamEvent.Content.Length > 0:
                        _sawContent = true;
                        _isThinking = false;
                        _partialContent.Append(streamEvent.Content);
                        break;

                    case ProviderStreamEventKind.Completed:
                        _usage = streamEvent.Usage;
                        _finishReason = streamEvent.FinishReason;
                        _isThinking = false;
                        break;
                }

                _updatedAt = DateTimeOffset.Now;
                return CreateUpdateUnsafe();
            }
        }

        public ConversationGenerationSessionUpdate End()
        {
            lock (_sync)
            {
                _isBusy = false;
                _isThinking = false;
                _updatedAt = DateTimeOffset.Now;
                return CreateUpdateUnsafe();
            }
        }

        public ConversationGenerationSessionUpdate Update()
        {
            lock (_sync)
            {
                return _currentUpdate ?? CreateUpdateUnsafe();
            }
        }

        private ConversationGenerationSessionUpdate CreateUpdateUnsafe()
        {
            var buffer = _partialContent;
            var length = buffer.Length;
            var metadata = new ConversationGenerationSession(
                ConversationId,
                OperationId,
                _isBusy,
                _messageId,
                _senderId,
                _replyKind,
                string.Empty,
                _isThinking,
                _sawReasoning,
                _sawContent,
                _usage,
                _finishReason,
                _updatedAt);
            return _currentUpdate = new ConversationGenerationSessionUpdate(metadata, () =>
            {
                // Capturing the prefix length, rather than reading the latest
                // session, keeps delayed versions correct across replies/cancellation.
                lock (_sync) return buffer.ToString(0, length);
            });
        }
    }
}
