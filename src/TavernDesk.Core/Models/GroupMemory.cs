namespace TavernDesk.Core.Models;

public enum GroupMemoryScope
{
    Shared = 0,
    Member = 1
}

public sealed class GroupMemoryBank
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string ConversationId { get; init; }
    public GroupMemoryScope Scope { get; init; }
    public string? CharacterId { get; init; }
    public string Body { get; set; } = string.Empty;
    public int TargetTokens { get; set; } = 5000;
    public long SourceThroughMessageSequence { get; set; }
    public string PromptVersion { get; set; } = "group-memory-v1";
    public long Revision { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class GroupMemoryCheckpoint
{
    public required string ConversationId { get; init; }
    public GroupMemoryScope Scope { get; init; }
    public string? CharacterId { get; init; }
    public long LastMessageSequence { get; set; }
    public int ProcessedMessages { get; set; }
    public string SourceDigest { get; set; } = string.Empty;
    public long Revision { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed record GroupMemoryWriteExpectation(
    GroupMemoryScope Scope,
    string? CharacterId,
    long? BankRevision,
    long? CheckpointRevision);

[Flags]
public enum GroupMemoryScopeMask
{
    None = 0,
    Shared = 1,
    Members = 2,
    All = Shared | Members
}

public enum GroupMemoryErrorCode
{
    None = 0,
    Cancelled,
    ConcurrentChange,
    ContextLimit,
    InvalidResponse,
    ProviderFailure,
    Unknown
}

public enum GroupMemoryUpdateStatus
{
    Updated,
    PartiallyUpdated,
    NoChanges,
    SkippedDisabled,
    SkippedNoAssignment,
    Failed
}

public sealed record GroupMemoryUpdateResult(
    string ConversationId,
    GroupMemoryUpdateStatus Status,
    long SourceThroughMessageSequence,
    bool Rebuilt = false,
    string? ErrorMessage = null,
    GroupMemoryScopeMask CompletedScopes = GroupMemoryScopeMask.None,
    GroupMemoryErrorCode ErrorCode = GroupMemoryErrorCode.None)
{
    public bool Succeeded =>
        Status is GroupMemoryUpdateStatus.Updated
            or GroupMemoryUpdateStatus.PartiallyUpdated
            or GroupMemoryUpdateStatus.NoChanges;
}
