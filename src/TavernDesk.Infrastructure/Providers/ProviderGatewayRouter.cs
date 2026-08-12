using System.Runtime.CompilerServices;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;
using TavernDesk.Infrastructure.Diagnostics;

namespace TavernDesk.Infrastructure.Providers;

public sealed class ProviderGatewayRouter :
    IProviderGateway,
    IEmbeddingModelCatalogGateway,
    IEmbeddingProviderGateway
{
    private readonly IProviderProfileRepository _profiles;
    private readonly IProviderGateway _openAiCompatible;
    private readonly IProviderGateway _grokCli;
    private readonly ITavernDeskDiagnostics _diagnostics;

    public ProviderGatewayRouter(
        IProviderProfileRepository profiles,
        IProviderGateway openAiCompatible,
        IProviderGateway grokCli,
        ITavernDeskDiagnostics? diagnostics = null)
    {
        _profiles = profiles;
        _openAiCompatible = openAiCompatible;
        _grokCli = grokCli;
        _diagnostics = diagnostics ?? NullTavernDeskDiagnostics.Instance;
    }

    public async Task<IReadOnlyList<ProviderModelDescriptor>> RefreshModelsAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveWithErrorLoggingAsync(
            providerId,
            "provider.models.resolve",
            cancellationToken);
        await using var trace = await _diagnostics.BeginApiTraceAsync(
            CreateMetadata(
                resolved.Profile,
                "model-catalog",
                modelId: null,
                "models"),
            new
            {
                method = "GET",
                provider_id = providerId
            },
            cancellationToken);
        try
        {
            var result = await resolved.Gateway.RefreshModelsAsync(
                providerId,
                cancellationToken);
            await trace.CompleteAsync(
                new
                {
                    models = result
                },
                CancellationToken.None);
            return result;
        }
        catch (OperationCanceledException exception)
            when (cancellationToken.IsCancellationRequested)
        {
            await trace.FailAsync(exception, CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            await trace.FailAsync(exception, CancellationToken.None);
            LogProviderError(
                "provider.models",
                resolved.Profile,
                modelId: null,
                exception);
            throw;
        }
    }

    public async Task<IReadOnlyList<ProviderModelDescriptor>> RefreshEmbeddingModelsAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveWithErrorLoggingAsync(
            providerId,
            "provider.embedding-models.resolve",
            cancellationToken);
        await using var trace = await _diagnostics.BeginApiTraceAsync(
            CreateMetadata(
                resolved.Profile,
                "embedding-model-catalog",
                modelId: null,
                "embeddings/models"),
            new
            {
                method = "GET",
                provider_id = providerId
            },
            cancellationToken);
        try
        {
            var result = resolved.Gateway
                         is IEmbeddingModelCatalogGateway embeddingGateway
                ? await embeddingGateway.RefreshEmbeddingModelsAsync(
                    providerId,
                    cancellationToken)
                : [];
            await trace.CompleteAsync(
                new
                {
                    models = result
                },
                CancellationToken.None);
            return result;
        }
        catch (OperationCanceledException exception)
            when (cancellationToken.IsCancellationRequested)
        {
            await trace.FailAsync(exception, CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            await trace.FailAsync(exception, CancellationToken.None);
            LogProviderError(
                "provider.embedding-models",
                resolved.Profile,
                modelId: null,
                exception);
            throw;
        }
    }

    public async Task<EmbeddingResponse> CreateEmbeddingsAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveWithErrorLoggingAsync(
            request.ProviderId,
            "provider.embeddings.resolve",
            cancellationToken);
        if (resolved.Gateway is not IEmbeddingProviderGateway embeddingGateway)
        {
            throw new NotSupportedException(
                "当前接入商不支持 OpenAI-compatible Embedding 接口。");
        }

        await using var trace = await _diagnostics.BeginApiTraceAsync(
            CreateMetadata(
                resolved.Profile,
                "embeddings",
                request.ModelId,
                "embeddings"),
            new
            {
                model = request.ModelId,
                input = request.Inputs,
                encoding_format = "float"
            },
            cancellationToken);
        try
        {
            var result = await embeddingGateway.CreateEmbeddingsAsync(
                request,
                cancellationToken);
            await trace.CompleteAsync(
                new
                {
                    vector_values_omitted = true,
                    vector_count = result.Vectors.Count,
                    dimensions = result.Vectors
                        .Select(vector => vector.Values.Count)
                        .ToArray(),
                    usage = result.Usage
                },
                CancellationToken.None);
            return result;
        }
        catch (OperationCanceledException exception)
            when (cancellationToken.IsCancellationRequested)
        {
            await trace.FailAsync(exception, CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            await trace.FailAsync(exception, CancellationToken.None);
            LogProviderError(
                "provider.embeddings",
                resolved.Profile,
                request.ModelId,
                exception);
            throw;
        }
    }

    public async IAsyncEnumerable<ProviderStreamEvent> StreamChatAsync(
        ModelExecutionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveWithErrorLoggingAsync(
            request.ProviderId,
            "provider.chat.resolve",
            cancellationToken);
        await using var trace = await _diagnostics.BeginApiTraceAsync(
            CreateMetadata(
                resolved.Profile,
                "chat-completions",
                request.ModelId,
                resolved.Profile.AdapterKind == ProviderAdapterKind.GrokCli
                    ? "session/prompt"
                    : "chat/completions"),
            new
            {
                model = request.ModelId,
                messages = request.Messages.Select(message => new
                {
                    role = message.Role,
                    content = message.Content
                }),
                max_tokens = request.MaxOutputTokens,
                temperature = request.Temperature,
                top_p = request.TopP,
                reasoning_enabled = request.ReasoningEnabled,
                session_id = request.SessionId,
                stream = true
            },
            cancellationToken);
        var healthGuard = new ProviderOutputHealthGuard();
        var enumerator = resolved.Gateway.StreamChatAsync(
                request,
                cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                ProviderStreamEvent item;
                try
                {
                    if (!await enumerator.MoveNextAsync())
                    {
                        break;
                    }

                    item = enumerator.Current;
                    if (item.Kind == ProviderStreamEventKind.Content)
                    {
                        healthGuard.Observe(item.Content);
                    }

                    trace.Observe(item);
                }
                catch (OperationCanceledException exception)
                    when (cancellationToken.IsCancellationRequested)
                {
                    await trace.FailAsync(exception, CancellationToken.None);
                    throw;
                }
                catch (Exception exception)
                {
                    await trace.FailAsync(exception, CancellationToken.None);
                    LogProviderError(
                        "provider.chat",
                        resolved.Profile,
                        request.ModelId,
                        exception);
                    throw;
                }

                yield return item;
            }

            await trace.CompleteAsync(
                responseBody: null,
                CancellationToken.None);
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    private async Task<ResolvedProvider> ResolveAsync(
        string providerId,
        CancellationToken cancellationToken)
    {
        var profile = await _profiles.GetAsync(providerId, cancellationToken)
                      ?? throw new InvalidOperationException(
                          "模型分配引用的接入商不存在。");
        if (!ProviderProfileIds.IsAdapterAllowed(
                profile.Id,
                profile.AdapterKind))
        {
            throw new NotSupportedException(
                profile.Id == ProviderProfileIds.GrokCli
                    ? "内置 Grok 接入商只能使用 Grok CLI（本机订阅登录）。"
                    : $"接入商“{profile.Name}”只能使用 OpenAI Chat Completions 兼容协议。");
        }

        var gateway = profile.AdapterKind switch
        {
            ProviderAdapterKind.OpenAiCompatible => _openAiCompatible,
            ProviderAdapterKind.GrokCli => _grokCli,
            _ => throw new NotSupportedException(
                $"接入商“{profile.Name}”使用了尚未实现的接口协议。")
        };
        return new ResolvedProvider(profile, gateway);
    }

    private async Task<ResolvedProvider> ResolveWithErrorLoggingAsync(
        string providerId,
        string category,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ResolveAsync(providerId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _diagnostics.LogError(
                category,
                exception,
                new Dictionary<string, object?>
                {
                    ["provider_id"] = providerId
                },
                includeExceptionMessage: false);
            throw;
        }
    }

    private static ApiTestTraceMetadata CreateMetadata(
        ProviderProfile profile,
        string operation,
        string? modelId,
        string relativeEndpoint)
    {
        var baseUrl = profile.BaseUrl.Trim().TrimEnd('/');
        return new ApiTestTraceMetadata(
            operation,
            profile.Id,
            profile.Name,
            modelId,
            profile.AdapterKind.ToString(),
            $"{baseUrl}/{relativeEndpoint.TrimStart('/')}");
    }

    private void LogProviderError(
        string category,
        ProviderProfile profile,
        string? modelId,
        Exception exception)
    {
        _diagnostics.LogError(
            category,
            exception,
            new Dictionary<string, object?>
            {
                ["provider_id"] = profile.Id,
                ["adapter"] = profile.AdapterKind.ToString(),
                ["model_id"] = modelId,
                ["http_status"] = exception is HttpRequestException http
                    ? (int?)http.StatusCode
                    : null
            },
            includeExceptionMessage: false);
    }

    private sealed record ResolvedProvider(
        ProviderProfile Profile,
        IProviderGateway Gateway);
}
