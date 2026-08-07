namespace TavernDesk.Core.Abstractions;

public interface ICampaignOperationGate
{
    Task<IAsyncDisposable> EnterGenerationAsync(
        string campaignId,
        CancellationToken cancellationToken = default);

    Task<IAsyncDisposable> EnterMemoryAsync(
        string campaignId,
        CancellationToken cancellationToken = default);
}
