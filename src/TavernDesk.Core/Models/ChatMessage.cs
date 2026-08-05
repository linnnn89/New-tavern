namespace TavernDesk.Core.Models;

public enum MessageSenderKind
{
    User,
    Character,
    System,
    Tool
}

public sealed class ChatMessage
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string ConversationId { get; init; }
    public long SequenceNo { get; set; }
    public MessageSenderKind SenderKind { get; init; }
    public string SenderId { get; init; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public int ActiveCandidateIndex { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class MessageCandidate
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string MessageId { get; init; }
    public int CandidateIndex { get; init; }
    public string Content { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
}
