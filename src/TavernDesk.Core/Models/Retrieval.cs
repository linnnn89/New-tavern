namespace TavernDesk.Core.Models;

public enum RetrievalScope
{
    CurrentConversation,
    SameCharacter
}

public sealed class RetrievalSettings
{
    public required string ConversationId { get; init; }
    public bool IsEnabled { get; set; } = true;
    public RetrievalScope Scope { get; set; } = RetrievalScope.CurrentConversation;
    public int RecentMessageCount { get; set; } = 20;
    public int MaximumResults { get; set; } = 6;
    public int TokenBudget { get; set; } = 1200;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed record RetrievalContextOptions(
    bool IsEnabled,
    RetrievalScope Scope,
    int RecentMessageCount,
    int MaximumResults,
    int TokenBudget,
    IReadOnlySet<string> ExcludedMessageIds);

public sealed record MessageRetrievalQuery(
    string ConversationId,
    string? CharacterId,
    RetrievalScope Scope,
    string QueryText,
    long? BeforeSequenceNo,
    int MaximumResults,
    IReadOnlySet<string> ExcludedMessageIds);

public sealed record RetrievedMessage(
    string MessageId,
    string ConversationId,
    string ConversationTitle,
    long SequenceNo,
    MessageSenderKind SenderKind,
    string SenderId,
    string Content,
    double Rank,
    DateTimeOffset CreatedAt);
