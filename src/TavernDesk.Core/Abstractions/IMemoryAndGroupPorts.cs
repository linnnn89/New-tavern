using TavernDesk.Core.Models;

namespace TavernDesk.Core.Abstractions;

public interface IMemoryWorkflowRepository
{
    Task<MemoryWorkflowSettings> GetSettingsAsync(
        string ownerId,
        CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(
        MemoryWorkflowSettings settings,
        CancellationToken cancellationToken = default);
    Task<MemoryCheckpoint?> GetCheckpointAsync(
        string ownerId,
        string conversationId,
        CancellationToken cancellationToken = default);
    Task<MemoryUpdateDraft?> GetDraftAsync(
        string targetOwnerId,
        string sourceConversationId,
        MemoryDraftKind kind,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MemoryUpdateDraft>> ListDraftsAsync(
        string sourceConversationId,
        CancellationToken cancellationToken = default);
    Task SaveDraftAsync(
        MemoryUpdateDraft draft,
        CancellationToken cancellationToken = default);
    Task CommitDraftAsync(
        string draftId,
        string editedBody,
        int targetTokens,
        CancellationToken cancellationToken = default);
    Task DeleteDraftAsync(
        string draftId,
        CancellationToken cancellationToken = default);
}

public interface IMemoryPromptComposer
{
    MemoryPromptPlan BuildUpdate(
        string ownerId,
        string conversationId,
        string currentMemory,
        int targetTokens,
        MemoryWorkflowSettings settings,
        MemoryCheckpoint? checkpoint,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyDictionary<string, string> senderNames,
        string memorySubject = "当前记忆主体",
        string userIdentity = "用户");

    MemoryPromptPlan BuildCompression(
        string ownerId,
        string conversationId,
        string currentMemory,
        int targetTokens,
        MemoryWorkflowSettings settings,
        MemoryCheckpoint? checkpoint,
        string memorySubject = "当前记忆主体",
        string userIdentity = "用户");

    MemoryPromptPlan BuildGroupMerge(
        string targetCharacterId,
        string characterName,
        string sourceConversationId,
        string characterMemory,
        string groupMemory,
        int targetTokens,
        GroupChatSettings settings,
        string userIdentity = "用户");
}

public interface IGroupChatRepository
{
    Task CreateAsync(
        Conversation conversation,
        GroupChatSettings settings,
        IReadOnlyList<GroupChatMember> members,
        CancellationToken cancellationToken = default);
    Task<GroupChatSettings?> GetSettingsAsync(
        string conversationId,
        CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(
        GroupChatSettings settings,
        CancellationToken cancellationToken = default);
    Task SaveConfigurationAsync(
        GroupChatSettings settings,
        IReadOnlyList<GroupChatMember> members,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GroupChatMember>> ListMembersAsync(
        string conversationId,
        CancellationToken cancellationToken = default);
    Task ReplaceMembersAsync(
        string conversationId,
        IReadOnlyList<GroupChatMember> members,
        CancellationToken cancellationToken = default);
    Task<GroupChatState> GetStateAsync(
        string conversationId,
        CancellationToken cancellationToken = default);
    Task SaveStateAsync(
        GroupChatState state,
        CancellationToken cancellationToken = default);
}

public interface IGroupMemoryRepository
{
    Task<GroupMemoryBank?> GetBankAsync(
        string conversationId,
        GroupMemoryScope scope,
        string? characterId = null,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GroupMemoryBank>> ListBanksAsync(
        string conversationId,
        CancellationToken cancellationToken = default);
    Task<GroupMemoryCheckpoint?> GetCheckpointAsync(
        string conversationId,
        GroupMemoryScope scope,
        string? characterId = null,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GroupMemoryCheckpoint>> ListCheckpointsAsync(
        string conversationId,
        CancellationToken cancellationToken = default);
    Task SaveBatchAsync(
        IReadOnlyList<GroupMemoryBank> banks,
        IReadOnlyList<GroupMemoryCheckpoint> checkpoints,
        CancellationToken cancellationToken = default);
    Task<bool> TrySaveBatchAsync(
        IReadOnlyList<GroupMemoryBank> banks,
        IReadOnlyList<GroupMemoryCheckpoint> checkpoints,
        IReadOnlyList<GroupMemoryWriteExpectation> expectations,
        CancellationToken cancellationToken = default);
    Task<bool> ClearIfConversationHasNoMessagesAsync(
        string conversationId,
        CancellationToken cancellationToken = default);
    Task InvalidateAsync(
        string conversationId,
        GroupMemoryScopeMask scopes = GroupMemoryScopeMask.All,
        CancellationToken cancellationToken = default);
}

public interface IGroupMemoryUpdateService
{
    Task<GroupMemoryUpdateResult> UpdateAsync(
        string conversationId,
        bool force = false,
        CancellationToken cancellationToken = default);
    Task InvalidateAsync(
        string conversationId,
        GroupMemoryScopeMask scopes = GroupMemoryScopeMask.All,
        CancellationToken cancellationToken = default);
}

public interface IGroupRelayPlanner
{
    GroupRelayDecision DecideNext(
        GroupChatSettings settings,
        IReadOnlyList<GroupChatMember> members,
        IReadOnlyDictionary<string, string> memberNames,
        IReadOnlyList<ChatMessage> messages,
        string personaName,
        string? manuallySelectedSpeakerId = null);
}
