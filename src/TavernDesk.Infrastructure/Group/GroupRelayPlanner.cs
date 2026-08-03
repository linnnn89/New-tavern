using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;

namespace TavernDesk.Infrastructure.Group;

public sealed class GroupRelayPlanner : IGroupRelayPlanner
{
    public GroupRelayDecision DecideNext(
        GroupChatSettings settings,
        IReadOnlyList<GroupChatMember> members,
        IReadOnlyDictionary<string, string> memberNames,
        IReadOnlyList<ChatMessage> messages,
        string personaName,
        string? manuallySelectedSpeakerId = null)
    {
        var enabled = members
            .Where(member => member.IsEnabled && memberNames.ContainsKey(member.CharacterId))
            .OrderBy(member => member.SortIndex)
            .ThenBy(member => member.CharacterId, StringComparer.Ordinal)
            .ToArray();
        if (enabled.Length == 0)
        {
            return new GroupRelayDecision(null, true, "群聊没有启用的角色。");
        }

        var last = messages
            .Where(message => !message.IsDeleted
                              && message.SenderKind is MessageSenderKind.User
                                  or MessageSenderKind.Character)
            .OrderBy(message => message.SequenceNo)
            .LastOrDefault();
        var lastSentence = LastSentence(last?.Content ?? string.Empty);
        if (last?.SenderKind == MessageSenderKind.Character
            && settings.PauseOnUserMention
            && (ContainsMention(lastSentence, "USER")
                || (!string.IsNullOrWhiteSpace(personaName)
                    && ContainsMention(lastSentence, personaName))))
        {
            return new GroupRelayDecision(
                null,
                true,
                $"检测到最后一句 @USER / @{personaName}，已等待用户回复。");
        }

        if (settings.RelayMode == GroupRelayMode.Manual)
        {
            return enabled.Any(member => member.CharacterId == manuallySelectedSpeakerId)
                ? new GroupRelayDecision(manuallySelectedSpeakerId, false, "使用手动选择的发言角色。")
                : new GroupRelayDecision(null, true, "手动模式需要先选择下一位发言角色。");
        }

        if (settings.RelayMode == GroupRelayMode.MentionDirected)
        {
            foreach (var member in enabled
                         .OrderByDescending(item => memberNames[item.CharacterId].Length))
            {
                if (ContainsMention(lastSentence, memberNames[member.CharacterId]))
                {
                    return new GroupRelayDecision(
                        member.CharacterId,
                        false,
                        $"最后一句指定 @{memberNames[member.CharacterId]}。");
                }
            }

            if (last?.SenderKind == MessageSenderKind.Character)
            {
                return new GroupRelayDecision(
                    null,
                    true,
                    "接力模式要求上一位角色在最后一句 @下一位角色，但没有识别到有效成员。");
            }

            if (enabled.Any(member => member.CharacterId == manuallySelectedSpeakerId))
            {
                return new GroupRelayDecision(
                    manuallySelectedSpeakerId,
                    false,
                    "用户消息后使用手动选择的首位发言角色。");
            }

            return new GroupRelayDecision(
                enabled[0].CharacterId,
                false,
                "用户消息后从群聊首位启用角色开始。");
        }

        var lastIndex = last?.SenderKind == MessageSenderKind.Character
            ? Array.FindIndex(enabled, member => member.CharacterId == last.SenderId)
            : -1;
        if (settings.RelayMode == GroupRelayMode.FixedOrder)
        {
            var nextIndex = (lastIndex + 1 + enabled.Length) % enabled.Length;
            return new GroupRelayDecision(
                enabled[nextIndex].CharacterId,
                false,
                "按固定成员顺序接力。");
        }

        var candidates = enabled.Length > 1 && lastIndex >= 0
            ? enabled.Where(member => member.CharacterId != last!.SenderId).ToArray()
            : enabled;
        return new GroupRelayDecision(
            candidates[Random.Shared.Next(candidates.Length)].CharacterId,
            false,
            "从启用成员中随机选择下一位。");
    }

    private static string LastSentence(string text)
    {
        var trimmed = text.Trim();
        while (trimmed.Length > 0
               && "。！？.!?\r\n".Contains(trimmed[^1], StringComparison.Ordinal))
        {
            trimmed = trimmed[..^1].TrimEnd();
        }

        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        var separator = trimmed.LastIndexOfAny(['。', '！', '？', '.', '!', '?', '\r', '\n']);
        return separator >= 0 ? trimmed[(separator + 1)..].Trim() : trimmed;
    }

    private static bool ContainsMention(string text, string name)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var needle = $"@{name.Trim()}";
        var start = 0;
        while (start < text.Length)
        {
            var index = text.IndexOf(needle, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return false;
            }

            var after = index + needle.Length;
            if (after == text.Length
                || !(char.IsLetterOrDigit(text[after]) || text[after] == '_'))
            {
                return true;
            }

            start = after;
        }

        return false;
    }
}
