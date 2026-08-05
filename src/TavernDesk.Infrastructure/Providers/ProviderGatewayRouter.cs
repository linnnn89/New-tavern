using System.Runtime.CompilerServices;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;

namespace TavernDesk.Infrastructure.Providers;

public sealed class ProviderGatewayRouter :
    IProviderGateway,
    IEmbeddingModelCatalogGateway,
    IEmbeddingProviderGateway
{
    private readonly IProviderProfileRepository _profiles;
    private readonly IProviderGateway _openAiCompatible;
    private readonly IProviderGateway _grokCli;

    public ProviderGatewayRouter(
        IProviderProfileRepository profiles,
        IProviderGateway openAiCompatible,
        IProviderGateway grokCli)
    {
        _profiles = profiles;
        _openAiCompatible = openAiCompatible;
        _grokCli = grokCli;
    }

    public async Task<IReadOnlyList<ProviderModelDescriptor>> RefreshModelsAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        var gateway = await ResolveAsync(providerId, cancellationToken);
        return await gateway.RefreshModelsAsync(providerId, cancellationToken);
    }

    public async Task<IReadOnlyList<ProviderModelDescriptor>> RefreshEmbeddingModelsAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        var gateway = await ResolveAsync(providerId, cancellationToken);
        return gateway is IEmbeddingModelCatalogGateway embeddingGateway
            ? await embeddingGateway.RefreshEmbeddingModelsAsync(
                providerId,
                cancellationToken)
            : [];
    }

    public async Task<EmbeddingResponse> CreateEmbeddingsAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        var gateway = await ResolveAsync(request.ProviderId, cancellationToken);
        if (gateway is not IEmbeddingProviderGateway embeddingGateway)
        {
            throw new NotSupportedException(
                "当前接入商不支持 OpenAI-compatible Embedding 接口。");
        }

        return await embeddingGateway.CreateEmbeddingsAsync(
            request,
            cancellationToken);
    }

    public async IAsyncEnumerable<ProviderStreamEvent> StreamChatAsync(
        ModelExecutionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var gateway = await ResolveAsync(request.ProviderId, cancellationToken);
        var healthGuard = new ProviderOutputHealthGuard();
        await foreach (var item in gateway.StreamChatAsync(
                           request,
                           cancellationToken))
        {
            if (item.Kind == ProviderStreamEventKind.Content)
            {
                healthGuard.Observe(item.Content);
            }

            yield return item;
        }
    }

    private async Task<IProviderGateway> ResolveAsync(
        string providerId,
        CancellationToken cancellationToken)
    {
        var profile = await _profiles.GetAsync(providerId, cancellationToken)
                      ?? throw new InvalidOperationException(
                          "模型分配引用的接入商不存在。");
        return profile.AdapterKind == ProviderAdapterKind.GrokCli
            ? _grokCli
            : _openAiCompatible;
    }
}
