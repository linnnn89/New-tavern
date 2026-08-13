using System.Security.Cryptography;
using System.Text;
using TavernDesk.Core.Models;

namespace TavernDesk.Infrastructure.Group;

internal static class GroupMemorySourceFingerprint
{
    public static string Compute(IEnumerable<ChatMessage> messages)
    {
        var builder = new StringBuilder();
        foreach (var message in messages.OrderBy(item => item.SequenceNo))
        {
            builder
                .Append(message.SequenceNo)
                .Append('\u001f')
                .Append((int)message.SenderKind)
                .Append('\u001f')
                .Append(message.SenderId)
                .Append('\u001f')
                .Append(message.Content)
                .Append('\u001e');
        }

        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}
