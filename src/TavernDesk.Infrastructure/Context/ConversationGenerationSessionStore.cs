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
    : IConversationGenerationSessionStore
{
    private readonly ConcurrentDictionary<string, SessionEntry> _active = new();
    private readonly ConcurrentDictionary<string, ConversationGenerationSession>
        _lastSnapshots = new();

    public event EventHandler<ConversationGenerationSession>? SessionChanged;

    public ConversationGenerationSession Get(string conversationId)
    {
        if (_active.TryGetValue(conversationId, out var active))
        {
            return active.Snapshot();
        }

        return _lastSnapshots.GetOrAdd(
            conversationId,
            static id => Empty(id));
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

        Publish(entry.Snapshot());
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

        _lastSnapshots.TryRemove(conversationId, out _);
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

    private void Publish(ConversationGenerationSession snapshot)
    {
        _lastSnapshots[snapshot.ConversationId] = snapshot;
        SessionChanged?.Invoke(this, snapshot);
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
        private readonly StringBuilder _partialContent = new();
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

        public ConversationGenerationSession BeginReply(
            string messageId,
            string senderId,
            LiveReplyKind replyKind)
        {
            lock (_sync)
            {
                _messageId = messageId;
                _senderId = senderId;
                _replyKind = replyKind;
                _partialContent.Clear();
                _isThinking = false;
                _sawReasoning = false;
                _sawContent = false;
                _usage = null;
                _finishReason = null;
                _updatedAt = DateTimeOffset.Now;
                return SnapshotUnsafe();
            }
        }

        public ConversationGenerationSession Apply(
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
                return SnapshotUnsafe();
            }
        }

        public ConversationGenerationSession End()
        {
            lock (_sync)
            {
                _isBusy = false;
                _isThinking = false;
                _updatedAt = DateTimeOffset.Now;
                return SnapshotUnsafe();
            }
        }

        public ConversationGenerationSession Snapshot()
        {
            lock (_sync)
            {
                return SnapshotUnsafe();
            }
        }

        private ConversationGenerationSession SnapshotUnsafe() =>
            new(
                ConversationId,
                OperationId,
                _isBusy,
                _messageId,
                _senderId,
                _replyKind,
                _partialContent.ToString(),
                _isThinking,
                _sawReasoning,
                _sawContent,
                _usage,
                _finishReason,
                _updatedAt);
    }
}
