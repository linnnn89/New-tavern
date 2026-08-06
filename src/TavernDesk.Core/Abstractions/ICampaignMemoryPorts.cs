using TavernDesk.Core.Models;

namespace TavernDesk.Core.Abstractions;

public interface ICampaignMemoryRepository
{
    Task<CampaignMemoryBank?> GetBankAsync(
        string campaignId,
        CampaignMemoryScope scope,
        CancellationToken cancellationToken = default);

    Task<CampaignMemoryCheckpoint?> GetCheckpointAsync(
        string campaignId,
        CampaignMemoryScope scope,
        CancellationToken cancellationToken = default);

    Task SaveBatchAsync(
        IReadOnlyList<CampaignMemoryBank> banks,
        IReadOnlyList<CampaignMemoryCheckpoint> checkpoints,
        CancellationToken cancellationToken = default);
}

public interface ICampaignMemoryUpdateService
{
    Task<CampaignMemoryUpdateResult> UpdateAsync(
        string campaignId,
        CancellationToken cancellationToken = default);
}
