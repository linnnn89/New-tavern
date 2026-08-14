namespace TavernDesk.Core.Models;

public enum GroupRelayMode
{
    Manual,
    FixedOrder,
    MentionDirected,
    Random
}

public sealed class GroupChatSettings
{
    public required string ConversationId { get; init; }
    public GroupRelayMode RelayMode { get; set; } = GroupRelayMode.MentionDirected;
    public bool AutoContinueEnabled { get; set; }
    public int MaximumAutomaticTurns { get; set; } = 8;
    public bool PauseOnUserMention { get; set; } = true;
    public bool MemberMemoryEnabled { get; set; }
    public int MemoryPendingTokenThreshold { get; set; } = 4000;
    public string GroupSystemPrompt { get; set; } = GroupPromptDefaults.SystemPrompt;
    public string MergeSystemPrompt { get; set; } = MemoryPromptDefaults.GroupMergeSystem;
    // Legacy persistence slot. Runtime composition always uses GroupMergeInput.
    public string MergeUserTemplate { get; set; } = MemoryPromptDefaults.GroupMergeInput;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class GroupChatMember
{
    public required string ConversationId { get; init; }
    public required string CharacterId { get; init; }
    public int SortIndex { get; set; }
    public bool IsEnabled { get; set; } = true;
}

public sealed class GroupChatState
{
    public required string ConversationId { get; init; }
    public string CurrentSpeakerId { get; set; } = string.Empty;
    public string NextSpeakerId { get; set; } = string.Empty;
    public int AutomaticTurns { get; set; }
    public bool IsPaused { get; set; }
    public string PauseReason { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed record GroupRelayDecision(
    string? NextSpeakerId,
    bool PauseForUser,
    string Reason);

public static class GroupPromptDefaults
{
    public const string SystemPrompt =
        """
        多角色群聊中只扮演本轮指定角色，保持其人设、知识边界和关系，不代替 USER 或其他角色发言。启用自动接力时，最后一句写 @下一位角色名；需要 USER 时写 @USER 或 @其 Persona 名。
        """;
}
