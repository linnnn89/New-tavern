using TavernDesk.Core.Models;

namespace TavernDesk.Core.Abstractions;

public interface IGlobalPromptConfiguration
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    string Get(GlobalPromptKey key);

    IReadOnlyDictionary<GlobalPromptKey, string> Snapshot();

    Task SaveAsync(
        IReadOnlyDictionary<GlobalPromptKey, string> values,
        CancellationToken cancellationToken = default);
}
