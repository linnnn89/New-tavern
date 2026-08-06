namespace TavernDesk.Core.Models;

public enum CampaignMemoryScope
{
    GameMaster,
    Public
}

public sealed class CampaignMemoryBank
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string CampaignId { get; init; }
    public CampaignMemoryScope Scope { get; init; }
    public string Body { get; set; } = string.Empty;
    public int TargetTokens { get; set; } = 5000;
    public long SourceThroughEventSequence { get; set; }
    public string PromptVersion { get; set; } = "campaign-memory-v1";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class CampaignMemoryCheckpoint
{
    public required string CampaignId { get; init; }
    public CampaignMemoryScope Scope { get; init; }
    public long LastEventSequence { get; set; }
    public int ProcessedRound { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public enum CampaignMemoryUpdateStatus
{
    Updated,
    NoChanges,
    SkippedNoAssignment,
    Failed
}

public sealed record CampaignMemoryUpdateResult(
    string CampaignId,
    CampaignMemoryUpdateStatus Status,
    long SourceThroughEventSequence,
    string? ErrorMessage = null)
{
    public bool Succeeded =>
        Status is CampaignMemoryUpdateStatus.Updated
            or CampaignMemoryUpdateStatus.NoChanges;
}
