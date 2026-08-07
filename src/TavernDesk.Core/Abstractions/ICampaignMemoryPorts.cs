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
    event EventHandler<CampaignMemoryUpdateProgress>? ProgressChanged;

    // Compatibility entry point for an explicit/manual update request. New
    // automatic callers must use the authoritative GM-resolution boundary
    // overload below.
    Task<CampaignMemoryUpdateResult> UpdateAsync(
        string campaignId,
        CancellationToken cancellationToken = default);

    Task<CampaignMemoryUpdateResult> UpdateAsync(
        string campaignId,
        long throughEventSequence,
        bool force = false,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(campaignId, cancellationToken);
}
