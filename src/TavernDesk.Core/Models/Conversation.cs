namespace TavernDesk.Core.Models;

public enum ConversationMode
{
    SingleCharacter,
    Group
}

public sealed class Conversation
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string? CharacterId { get; init; }
    public string Title { get; set; } = "新对话";
    public ConversationMode Mode { get; init; } = ConversationMode.SingleCharacter;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed record ConversationSummary(
    string Id,
    string? CharacterId,
    string Title,
    ConversationMode Mode,
    string LastMessagePreview,
    DateTimeOffset UpdatedAt);
