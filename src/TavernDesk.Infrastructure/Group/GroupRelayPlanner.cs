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
        // The visible product now follows SillyTavern's "List Order" plus
        // "Force Talk" model: avatar clicks bypass this planner, while the
        // automatic route is always deterministic member order.  Keep the
        // legacy Manual enum branch for old callers and persisted settings;
        // MentionDirected and Random intentionally fall through to fixed
        // order instead of interpreting model-produced @ text.
        if (settings.RelayMode == GroupRelayMode.Manual)
        {
            return enabled.Any(member => member.CharacterId == manuallySelectedSpeakerId)
                ? new GroupRelayDecision(manuallySelectedSpeakerId, false, "使用手动选择的发言角色。")
                : new GroupRelayDecision(null, true, "手动模式需要先选择下一位发言角色。");
        }

        var lastIndex = last?.SenderKind == MessageSenderKind.Character
            ? Array.FindIndex(enabled, member => member.CharacterId == last.SenderId)
            : -1;
        var nextIndex = (lastIndex + 1 + enabled.Length) % enabled.Length;
        return new GroupRelayDecision(
            enabled[nextIndex].CharacterId,
            false,
            "按固定成员顺序接力。");
    }
}
