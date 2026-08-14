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
        string personaName)
    {
        var enabled = members
            .Where(member => member.IsEnabled && memberNames.ContainsKey(member.CharacterId))
            .OrderBy(member => member.SortIndex)
            .ThenBy(member => member.CharacterId, StringComparer.Ordinal)
            .ToArray();
        if (enabled.Length == 0)
        {
            return new GroupRelayDecision(null, true, "group-no-enabled");
        }

        var last = messages
            .Where(message => !message.IsDeleted
                              && message.SenderKind is MessageSenderKind.User
                                  or MessageSenderKind.Character)
            .OrderBy(message => message.SequenceNo)
            .LastOrDefault();
        var lastIndex = last?.SenderKind == MessageSenderKind.Character
            ? Array.FindIndex(enabled, member => member.CharacterId == last.SenderId)
            : -1;
        var nextIndex = (lastIndex + 1 + enabled.Length) % enabled.Length;
        return new GroupRelayDecision(
            enabled[nextIndex].CharacterId,
            false,
            "group-fixed-order");
    }
}
