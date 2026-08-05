using TavernDesk.Core.Models;

namespace TavernDesk.Core.Abstractions;

public interface IMessageRetrievalRepository
{
    Task<RetrievalSettings> GetSettingsAsync(
        string conversationId,
        CancellationToken cancellationToken = default);

    Task SaveSettingsAsync(
        RetrievalSettings settings,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RetrievedMessage>> SearchAsync(
        MessageRetrievalQuery query,
        CancellationToken cancellationToken = default);

    Task RebuildIndexAsync(CancellationToken cancellationToken = default);
}
