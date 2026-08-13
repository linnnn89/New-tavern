namespace TavernDesk.Core.Models;

public sealed class MemoryBank
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string OwnerId { get; init; }
    public string Body { get; set; } = string.Empty;
    public int TargetTokens { get; set; } = 5000;
    public long Revision { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}
