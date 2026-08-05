using System.Text;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;

namespace TavernDesk.Infrastructure.Memory;

public sealed class MemoryPromptComposer : IMemoryPromptComposer
{
    public MemoryPromptPlan BuildUpdate(
        string ownerId,
        string conversationId,
        string currentMemory,
        int targetTokens,
        MemoryWorkflowSettings settings,
        MemoryCheckpoint? checkpoint,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyDictionary<string, string> senderNames)
    {
        ValidateTargetTokens(targetTokens);
        var fromSequence = settings.SendOnlyNewMessages
            ? checkpoint?.LastSequenceNo ?? 0
            : 0;
        var source = messages
            .Where(message => !message.IsDeleted
                              && message.SequenceNo > fromSequence
                              && message.SenderKind is MessageSenderKind.User
                                  or MessageSenderKind.Character)
            .OrderBy(message => message.SequenceNo)
            .ToArray();
        if (source.Length == 0)
        {
            throw new InvalidOperationException(
                settings.SendOnlyNewMessages
                    ? "当前检查点之后没有可更新的聊天记录。"
                    : "当前聊天没有可更新的记录。");
        }

        source = LimitSource(
            source,
            settings.MaximumSourceUserTurns,
            settings.SendOnlyNewMessages);

        var transcript = new StringBuilder();
        foreach (var message in source)
        {
            var sender = message.SenderKind == MessageSenderKind.User
                ? "USER"
                : senderNames.GetValueOrDefault(message.SenderId, "角色");
            transcript
                .Append('[')
                .Append(sender)
                .Append(" #")
                .Append(message.SequenceNo)
                .AppendLine("]")
                .AppendLine(message.Content.Trim())
                .AppendLine();
        }

        var inputPayload = Expand(
            MemoryPromptDefaults.UpdateInput,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["target_tokens"] = targetTokens.ToString(),
                ["current_memory"] = NormalizeEmpty(currentMemory),
                ["new_messages"] = transcript.ToString().TrimEnd()
            });
        return new MemoryPromptPlan(
            MemoryDraftKind.Update,
            ownerId,
            conversationId,
            settings.UpdateSystemPrompt.Trim(),
            inputPayload,
            source.Max(message => message.SequenceNo),
            source.Count(message => message.SenderKind == MessageSenderKind.User),
            targetTokens);
    }

    private static ChatMessage[] LimitSource(
        ChatMessage[] source,
        int maximumUserTurns,
        bool sendOnlyNewMessages)
    {
        var limit = Math.Clamp(maximumUserTurns, 1, 10000);
        var userIndexes = source
            .Select((message, index) =>
                (message.SenderKind == MessageSenderKind.User, index))
            .Where(item => item.Item1)
            .Select(item => item.index)
            .ToArray();
        if (userIndexes.Length <= limit)
        {
            return source;
        }

        if (sendOnlyNewMessages)
        {
            // Consume the oldest pending turns first. The boundary is the
            // next user message, so the final selected turn keeps all of its
            // replies.
            var nextUserIndex = userIndexes[limit];
            return source[..nextUserIndex];
        }

        // When the option is disabled, use the most recent batch while still
        // keeping each selected turn intact through the end of the transcript.
        return source[userIndexes[^limit]..];
    }

    public MemoryPromptPlan BuildCompression(
        string ownerId,
        string conversationId,
        string currentMemory,
        int targetTokens,
        MemoryWorkflowSettings settings,
        MemoryCheckpoint? checkpoint)
    {
        ValidateTargetTokens(targetTokens);
        if (string.IsNullOrWhiteSpace(currentMemory))
        {
            throw new InvalidOperationException("当前记忆银行为空，没有可压缩的正文。");
        }

        return new MemoryPromptPlan(
            MemoryDraftKind.Compression,
            ownerId,
            conversationId,
            settings.CompressionSystemPrompt.Trim(),
            Expand(
                MemoryPromptDefaults.CompressionInput,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["target_tokens"] = targetTokens.ToString(),
                    ["current_memory"] = currentMemory.Trim()
                }),
            checkpoint?.LastSequenceNo ?? 0,
            0,
            targetTokens);
    }

    public MemoryPromptPlan BuildGroupMerge(
        string targetCharacterId,
        string characterName,
        string sourceConversationId,
        string characterMemory,
        string groupMemory,
        int targetTokens,
        GroupChatSettings settings)
    {
        ValidateTargetTokens(targetTokens);
        if (string.IsNullOrWhiteSpace(groupMemory))
        {
            throw new InvalidOperationException("当前群聊记忆为空，没有可合并的辅助记忆。");
        }

        return new MemoryPromptPlan(
            MemoryDraftKind.GroupMerge,
            targetCharacterId,
            sourceConversationId,
            settings.MergeSystemPrompt.Trim(),
            Expand(
                MemoryPromptDefaults.GroupMergeInput,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["target_tokens"] = targetTokens.ToString(),
                    ["character_memory"] = NormalizeEmpty(characterMemory),
                    ["group_memory"] = groupMemory.Trim(),
                    ["character_name"] = characterName
                }),
            0,
            0,
            targetTokens);
    }

    private static string Expand(
        string template,
        IReadOnlyDictionary<string, string> values)
    {
        var result = template;
        foreach (var (key, value) in values)
        {
            result = result.Replace(
                $"{{{key}}}",
                value,
                StringComparison.Ordinal);
        }

        return result.Trim();
    }

    private static string NormalizeEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? "（空）" : value.Trim();

    private static void ValidateTargetTokens(int targetTokens)
    {
        if (targetTokens is < 1000 or > 20000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetTokens),
                "记忆目标必须在 1000–20000 tokens 之间。");
        }
    }
}
