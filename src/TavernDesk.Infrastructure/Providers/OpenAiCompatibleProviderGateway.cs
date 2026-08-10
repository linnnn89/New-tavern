using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;

namespace TavernDesk.Infrastructure.Providers;

public sealed class OpenAiCompatibleProviderGateway :
    IProviderGateway,
    IEmbeddingModelCatalogGateway,
    IEmbeddingProviderGateway
{
    private const int MaximumModelsResponseBytes = 8 * 1024 * 1024;
    private const int MaximumErrorResponseBytes = 64 * 1024;
    private const int MaximumStreamLineCharacters = 2 * 1024 * 1024;
    private const int MaximumDisplayedErrorCharacters = 800;
    private readonly IProviderProfileRepository _profiles;
    private readonly ISecretStore _secrets;
    private readonly HttpClient _httpClient;

    public OpenAiCompatibleProviderGateway(
        IProviderProfileRepository profiles,
        ISecretStore secrets,
        HttpClient? httpClient = null)
    {
        _profiles = profiles;
        _secrets = secrets;
        _httpClient = httpClient ?? CreateHttpClient();
    }

    public Task<IReadOnlyList<ProviderModelDescriptor>> RefreshModelsAsync(
        string providerId,
        CancellationToken cancellationToken = default) =>
        RefreshCatalogAsync(
            providerId,
            "models",
            forcedKind: null,
            cancellationToken);

    public Task<IReadOnlyList<ProviderModelDescriptor>> RefreshEmbeddingModelsAsync(
        string providerId,
        CancellationToken cancellationToken = default) =>
        RefreshCatalogAsync(
            providerId,
            "embeddings/models",
            ModelCatalogKind.Embedding,
            cancellationToken);

    public async Task<EmbeddingResponse> CreateEmbeddingsAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Inputs.Count == 0)
        {
            throw new ArgumentException(
                "Embedding 请求至少需要一条输入文本。",
                nameof(request));
        }

        var profile = await ResolveProfileAsync(
            request.ProviderId,
            cancellationToken);
        using var timeout = CreateTimeout(profile, cancellationToken);
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            BuildEndpoint(profile.BaseUrl, "embeddings"));
        await AddAuthorizationAsync(message, profile, timeout.Token);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.ModelId,
            ["input"] = request.Inputs.Count == 1
                ? request.Inputs[0]
                : request.Inputs.ToArray(),
            ["encoding_format"] = "float"
        };
        message.Content = new ByteArrayContent(
            JsonSerializer.SerializeToUtf8Bytes(payload));
        message.Content.Headers.ContentType =
            new MediaTypeHeaderValue("application/json");
        using var response = await SendWithDiagnosticsAsync(
            message,
            profile,
            timeout,
            cancellationToken);
        await EnsureSuccessAsync(response, timeout.Token);
        var bytes = await ExecuteTransportAsync(
            token => ReadLimitedAsync(
                response.Content,
                MaximumModelsResponseBytes,
                token),
            profile,
            timeout,
            cancellationToken);
        return ReadEmbeddingResponse(bytes);
    }

    private async Task<IReadOnlyList<ProviderModelDescriptor>> RefreshCatalogAsync(
        string providerId,
        string endpointPath,
        ModelCatalogKind? forcedKind,
        CancellationToken cancellationToken)
    {
        var profile = await ResolveProfileAsync(providerId, cancellationToken);
        using var timeout = CreateTimeout(profile, cancellationToken);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            BuildEndpoint(profile.BaseUrl, endpointPath));
        await AddAuthorizationAsync(request, profile, timeout.Token);
        using var response = await SendWithDiagnosticsAsync(
            request,
            profile,
            timeout,
            cancellationToken);
        await EnsureSuccessAsync(response, timeout.Token);
        var bytes = await ExecuteTransportAsync(
            token => ReadLimitedAsync(
                response.Content,
                MaximumModelsResponseBytes,
                token),
            profile,
            timeout,
            cancellationToken);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                MaxDepth = 64
            });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "模型目录返回的 JSON 无法解析。",
                exception);
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("模型目录响应缺少 data 数组。");
            }

            return data.EnumerateArray()
                .Select(element => ReadModel(element, forcedKind))
                .Where(model => model is not null)
                .Cast<ProviderModelDescriptor>()
                .GroupBy(model => model.ModelId, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public async IAsyncEnumerable<ProviderStreamEvent> StreamChatAsync(
        ModelExecutionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var profile = await ResolveProfileAsync(request.ProviderId, cancellationToken);
        using var timeout = CreateTimeout(profile, cancellationToken);
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            BuildEndpoint(profile.BaseUrl, "chat/completions"));
        await AddAuthorizationAsync(message, profile, timeout.Token);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.ModelId,
            ["messages"] = request.Messages.Select(item => new
            {
                role = item.Role,
                content = item.Content
            }).ToArray(),
            ["temperature"] = request.Temperature,
            ["top_p"] = request.TopP,
            ["max_tokens"] = request.MaxOutputTokens,
            ["stream"] = true,
            ["stream_options"] = new
            {
                include_usage = true
            }
        };
        if (request.ReasoningEnabled is { } reasoningEnabled
            && ModelFeatureSupport.SupportsOpenRouterDeepSeekReasoning(
                profile,
                request.ModelId))
        {
            payload["reasoning"] = reasoningEnabled
                ? new { enabled = true }
                : new { effort = "none" };
        }
        if (ModelFeatureSupport.IsOpenRouter(profile)
            && !string.IsNullOrWhiteSpace(request.SessionId))
        {
            payload["session_id"] = request.SessionId.Trim();
        }

        message.Content = new ByteArrayContent(
            JsonSerializer.SerializeToUtf8Bytes(payload));
        message.Content.Headers.ContentType =
            new MediaTypeHeaderValue("application/json");
        using var response = await SendWithDiagnosticsAsync(
            message,
            profile,
            timeout,
            cancellationToken);
        await EnsureSuccessAsync(response, timeout.Token);

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = await ExecuteTransportAsync(
                token => ReadLimitedAsync(
                    response.Content,
                    MaximumModelsResponseBytes,
                    token),
                profile,
                timeout,
                cancellationToken);
            var parsed = ReadNonStreamingEvent(bytes);
            var jsonNormalizer = new ReasoningStreamNormalizer();
            var normalized = jsonNormalizer.Push(
                parsed.Content,
                parsed.StructuredContainer);
            var tail = jsonNormalizer.Complete();
            if (normalized.HasReasoning || tail.HasReasoning)
            {
                yield return new ProviderStreamEvent(ProviderStreamEventKind.Reasoning);
            }

            var content = normalized.Content + tail.Content;
            if (content.Length > 0)
            {
                yield return new ProviderStreamEvent(
                    ProviderStreamEventKind.Content,
                    content);
            }

            yield return new ProviderStreamEvent(
                ProviderStreamEventKind.Completed,
                Usage: parsed.Usage,
                FinishReason: parsed.FinishReason);
            yield break;
        }

        var reasoningSignaled = false;
        var completionYielded = false;
        var normalizer = new ReasoningStreamNormalizer();
        ProviderTokenUsage? usage = null;
        string? finishReason = null;
        await using var stream = await ExecuteTransportAsync(
            response.Content.ReadAsStreamAsync,
            profile,
            timeout,
            cancellationToken);
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: true,
            leaveOpen: false);
        while (true)
        {
            var line = await ExecuteTransportAsync(
                token => reader.ReadLineAsync(token).AsTask(),
                profile,
                timeout,
                cancellationToken);
            if (line is null)
            {
                break;
            }

            if (line.Length > MaximumStreamLineCharacters)
            {
                throw new InvalidDataException("流式响应单行超过 2 MiB 安全上限。");
            }

            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var data = line[5..].TrimStart();
            if (data == "[DONE]")
            {
                var tail = normalizer.Complete();
                if (tail.HasReasoning && !reasoningSignaled)
                {
                    reasoningSignaled = true;
                    yield return new ProviderStreamEvent(
                        ProviderStreamEventKind.Reasoning);
                }

                if (tail.Content.Length > 0)
                {
                    yield return new ProviderStreamEvent(
                        ProviderStreamEventKind.Content,
                        tail.Content);
                }

                yield return new ProviderStreamEvent(
                    ProviderStreamEventKind.Completed,
                    Usage: usage,
                    FinishReason: finishReason);
                completionYielded = true;
                break;
            }

            if (data.Length == 0)
            {
                continue;
            }

            var parsed = ReadStreamingEvent(data);
            usage = parsed.Usage ?? usage;
            finishReason = parsed.FinishReason ?? finishReason;
            var normalized = normalizer.Push(
                parsed.Content,
                parsed.StructuredContainer);
            if (normalized.HasReasoning && !reasoningSignaled)
            {
                reasoningSignaled = true;
                yield return new ProviderStreamEvent(ProviderStreamEventKind.Reasoning);
            }

            if (normalized.Content.Length > 0)
            {
                yield return new ProviderStreamEvent(
                    ProviderStreamEventKind.Content,
                    normalized.Content);
            }
        }

        if (!completionYielded)
        {
            var tail = normalizer.Complete();
            if (tail.HasReasoning && !reasoningSignaled)
            {
                yield return new ProviderStreamEvent(
                    ProviderStreamEventKind.Reasoning);
            }

            if (tail.Content.Length > 0)
            {
                yield return new ProviderStreamEvent(
                    ProviderStreamEventKind.Content,
                    tail.Content);
            }

            yield return new ProviderStreamEvent(
                ProviderStreamEventKind.Completed,
                Usage: usage,
                FinishReason: finishReason);
        }
    }

    private async Task<ProviderProfile> ResolveProfileAsync(
        string providerId,
        CancellationToken cancellationToken)
    {
        var profile = await _profiles.GetAsync(providerId, cancellationToken)
                      ?? throw new InvalidOperationException("模型分配引用的接入商不存在。");
        if (!profile.IsEnabled)
        {
            throw new InvalidOperationException($"接入商“{profile.Name}”已停用。");
        }

        if (profile.AdapterKind != ProviderAdapterKind.OpenAiCompatible
            || profile.Id == ProviderProfileIds.GrokCli)
        {
            throw new NotSupportedException(
                $"接入商“{profile.Name}”不是 OpenAI Chat Completions 兼容适配器。");
        }

        return profile;
    }

    private async Task AddAuthorizationAsync(
        HttpRequestMessage request,
        ProviderProfile profile,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profile.SecretReference))
        {
            return;
        }

        var secret = await _secrets.ReadAsync(
            profile.SecretReference,
            cancellationToken);
        if (!string.IsNullOrWhiteSpace(secret))
        {
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", secret);
        }
    }

    private static ProviderModelDescriptor? ReadModel(
        JsonElement element,
        ModelCatalogKind? forcedKind)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("id", out var idElement)
            || idElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var id = idElement.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(id) || id.Length > 512)
        {
            return null;
        }

        var displayName = element.TryGetProperty("name", out var nameElement)
                          && nameElement.ValueKind == JsonValueKind.String
            ? nameElement.GetString()?.Trim()
            : id;
        var contextLimit = ReadPositiveInt32(element, "context_length");
        int? maxOutputTokens = null;
        if (element.TryGetProperty("top_provider", out var topProvider)
            && topProvider.ValueKind == JsonValueKind.Object)
        {
            contextLimit ??= ReadPositiveInt32(topProvider, "context_length");
            maxOutputTokens =
                ReadPositiveInt32(topProvider, "max_completion_tokens");
        }

        maxOutputTokens ??=
            ReadPositiveInt32(element, "max_completion_tokens");
        return new ProviderModelDescriptor(
            id,
            string.IsNullOrWhiteSpace(displayName) ? id : displayName,
            contextLimit,
            maxOutputTokens,
            ModelKind: forcedKind ?? ModelCatalogKind.Chat);
    }

    private static int? ReadPositiveInt32(
        JsonElement element,
        string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var parsed)
            || parsed is <= 0 or > int.MaxValue)
        {
            return null;
        }

        return (int)parsed;
    }

    private static ParsedProviderChunk ReadStreamingEvent(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            MaxDepth = 64
        });
        ThrowIfApiError(document.RootElement);
        var usage = ReadUsage(document.RootElement);
        if (!document.RootElement.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            return new ParsedProviderChunk(
                Content: string.Empty,
                StructuredContainer: default,
                FinishReason: null,
                Usage: usage);
        }

        var choice = choices[0];
        var finishReason = ReadString(choice, "finish_reason");
        var hasDelta = choice.TryGetProperty("delta", out var delta)
                       && delta.ValueKind == JsonValueKind.Object;
        return new ParsedProviderChunk(
            Content: hasDelta ? ReadString(delta, "content") ?? string.Empty : string.Empty,
            StructuredContainer: hasDelta ? delta.Clone() : default,
            FinishReason: finishReason,
            Usage: usage);
    }

    private static ParsedProviderChunk ReadNonStreamingEvent(byte[] json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            MaxDepth = 64
        });
        ThrowIfApiError(document.RootElement);
        if (!document.RootElement.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0
            || !choices[0].TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Chat Completions 响应缺少 choices[0].message。");
        }

        return new ParsedProviderChunk(
            Content: ReadString(message, "content") ?? string.Empty,
            StructuredContainer: message.Clone(),
            FinishReason: ReadString(choices[0], "finish_reason"),
            Usage: ReadUsage(document.RootElement));
    }

    private static EmbeddingResponse ReadEmbeddingResponse(byte[] json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            MaxDepth = 64
        });
        ThrowIfApiError(document.RootElement);
        if (!document.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                "Embedding 响应缺少 data 数组。");
        }

        var vectors = new List<EmbeddingVector>();
        var fallbackIndex = 0;
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("embedding", out var embedding)
                || embedding.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException(
                    "Embedding 响应缺少有效的 embedding 数组。");
            }

            var values = new List<float>();
            foreach (var value in embedding.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.Number
                    || !value.TryGetSingle(out var parsed)
                    || !float.IsFinite(parsed))
                {
                    throw new InvalidDataException(
                        "Embedding 响应包含无效的向量值。");
                }

                values.Add(parsed);
            }

            var index = item.TryGetProperty("index", out var indexElement)
                        && indexElement.ValueKind == JsonValueKind.Number
                        && indexElement.TryGetInt32(out var parsedIndex)
                ? parsedIndex
                : fallbackIndex;
            vectors.Add(new EmbeddingVector(index, values));
            fallbackIndex++;
        }

        return new EmbeddingResponse(
            vectors.OrderBy(vector => vector.Index).ToArray(),
            ReadUsage(document.RootElement));
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static ProviderTokenUsage? ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage)
            || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var promptTokens = ReadTokenCount(usage, "prompt_tokens");
        var completionTokens = ReadTokenCount(usage, "completion_tokens");
        var totalTokens = ReadTokenCount(usage, "total_tokens");
        int? reasoningTokens = null;
        if (usage.TryGetProperty("completion_tokens_details", out var details)
            && details.ValueKind == JsonValueKind.Object
            && details.TryGetProperty("reasoning_tokens", out var reasoning)
            && reasoning.ValueKind == JsonValueKind.Number
            && reasoning.TryGetInt32(out var parsedReasoning))
        {
            reasoningTokens = parsedReasoning;
        }
        var cachedPromptTokens = ReadOptionalTokenCount(
            usage,
            "prompt_cache_hit_tokens");
        var uncachedPromptTokens = ReadOptionalTokenCount(
            usage,
            "prompt_cache_miss_tokens");
        if (usage.TryGetProperty("prompt_tokens_details", out var promptDetails)
            && promptDetails.ValueKind == JsonValueKind.Object)
        {
            cachedPromptTokens ??= ReadOptionalTokenCount(
                promptDetails,
                "cached_tokens");
        }
        if (uncachedPromptTokens is null && cachedPromptTokens is { } cached)
        {
            uncachedPromptTokens = Math.Max(0, promptTokens - cached);
        }

        return new ProviderTokenUsage(
            promptTokens,
            completionTokens,
            totalTokens > 0 ? totalTokens : promptTokens + completionTokens,
            reasoningTokens,
            cachedPromptTokens,
            uncachedPromptTokens);
    }

    private static int ReadTokenCount(JsonElement usage, string propertyName) =>
        usage.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var parsed)
            ? parsed
            : 0;

    private static int? ReadOptionalTokenCount(
        JsonElement usage,
        string propertyName) =>
        usage.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    private sealed record ParsedProviderChunk(
        string Content,
        JsonElement StructuredContainer,
        string? FinishReason,
        ProviderTokenUsage? Usage);

    private static void ThrowIfApiError(JsonElement root)
    {
        var error = ReadProviderError(root);
        if (error is null)
        {
            return;
        }

        throw new InvalidOperationException(
            FormatProviderError(
                "接入商在生成过程中返回错误",
                error));
    }

    private static Uri BuildEndpoint(string baseUrl, string relativePath)
    {
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var baseUri)
            || baseUri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("接入商 API 地址无效。");
        }

        if (!string.IsNullOrEmpty(baseUri.Query)
            || !string.IsNullOrEmpty(baseUri.Fragment))
        {
            throw new InvalidOperationException("接入商 API 地址不能包含查询参数或片段。");
        }

        var builder = new UriBuilder(baseUri);
        var path = builder.Path.TrimEnd('/');
        // Preserve explicit compatible paths such as /api/v1 or /v1. Only a
        // bare host receives the common /v1 default.
        if (path.Length == 0)
        {
            path = "/v1";
        }

        builder.Path = path + "/";
        return new Uri(builder.Uri, relativePath.TrimStart('/'));
    }

    private static CancellationTokenSource CreateTimeout(
        ProviderProfile profile,
        CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(
            Math.Clamp(profile.RequestTimeoutSeconds, 1, 3600)));
        return timeout;
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var bytes = await ReadLimitedAsync(
            response.Content,
            MaximumErrorResponseBytes,
            cancellationToken);
        var error = TryReadProviderError(bytes);
        var summary = FriendlyHttpStatus(response.StatusCode);
        var detail = error is null
            ? CleanProviderText(Encoding.UTF8.GetString(bytes))
            : FormatProviderError(summary, error);
        var retryAfter = ReadRetryAfter(response);
        throw new HttpRequestException(
            $"接入商请求失败：{summary}（HTTP {(int)response.StatusCode}）"
            + (detail.Length == 0 || detail.StartsWith(summary, StringComparison.Ordinal)
                ? string.Empty
                : $"；{detail}")
            + retryAfter,
            inner: null,
            response.StatusCode);
    }

    private async Task<HttpResponseMessage> SendWithDiagnosticsAsync(
        HttpRequestMessage request,
        ProviderProfile profile,
        CancellationTokenSource timeout,
        CancellationToken callerCancellation)
    {
        return await ExecuteTransportAsync(
            token => _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                token),
            profile,
            timeout,
            callerCancellation);
    }

    private static async Task<T> ExecuteTransportAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ProviderProfile profile,
        CancellationTokenSource timeout,
        CancellationToken callerCancellation)
    {
        try
        {
            return await operation(timeout.Token);
        }
        catch (OperationCanceledException exception)
            when (!callerCancellation.IsCancellationRequested
                  && timeout.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"等待接入商“{profile.Name}”超过 "
                + $"{profile.RequestTimeoutSeconds:0.###} 秒。",
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new HttpRequestException(
                $"无法连接接入商“{profile.Name}”："
                + CleanProviderText(exception.Message),
                exception,
                exception.StatusCode);
        }
        catch (IOException exception)
        {
            throw new IOException(
                $"读取接入商“{profile.Name}”响应失败："
                + CleanProviderText(exception.Message),
                exception);
        }
    }

    private static ProviderErrorDetail? TryReadProviderError(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                MaxDepth = 64
            });
            return ReadProviderError(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ProviderErrorDetail? ReadProviderError(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var error)
            || error.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (error.ValueKind == JsonValueKind.String)
        {
            return new ProviderErrorDetail(
                CleanProviderText(error.GetString()),
                string.Empty);
        }

        if (error.ValueKind != JsonValueKind.Object)
        {
            return new ProviderErrorDetail("接入商返回了未识别的错误。", string.Empty);
        }

        var message = ReadString(error, "message");
        var type = ReadString(error, "type")
                   ?? ReadString(error, "error_type");
        if (error.TryGetProperty("metadata", out var metadata)
            && metadata.ValueKind == JsonValueKind.Object)
        {
            type ??= ReadString(metadata, "error_type");
        }

        if (type is null
            && error.TryGetProperty("code", out var code)
            && code.ValueKind is JsonValueKind.String or JsonValueKind.Number)
        {
            type = code.ToString();
        }

        return new ProviderErrorDetail(
            CleanProviderText(message),
            CleanProviderText(type));
    }

    private static string FormatProviderError(
        string summary,
        ProviderErrorDetail error)
    {
        var category = FriendlyErrorType(error.Type);
        var result = string.Empty;
        if (category.Length > 0
            && !string.Equals(
                category,
                summary,
                StringComparison.OrdinalIgnoreCase))
        {
            result = category;
        }

        if (error.Message.Length > 0
            && !string.Equals(error.Message, category, StringComparison.OrdinalIgnoreCase))
        {
            result += (result.Length == 0 ? string.Empty : "；")
                      + $"服务返回：{error.Message}";
        }

        if (error.Type.Length > 0)
        {
            result += (result.Length == 0 ? string.Empty : " ")
                      + $"〔{error.Type}〕";
        }

        return result;
    }

    private static string FriendlyHttpStatus(HttpStatusCode statusCode) =>
        statusCode switch
        {
            HttpStatusCode.BadRequest => "请求参数或上下文不被模型接受",
            HttpStatusCode.Unauthorized => "API Key 无效、已撤销或缺少",
            HttpStatusCode.PaymentRequired => "账户余额或额度不足",
            HttpStatusCode.Forbidden => "API Key 权限不足或请求被安全策略拒绝",
            HttpStatusCode.NotFound => "API 地址、模型或接口不存在",
            HttpStatusCode.RequestTimeout => "服务端等待超时",
            HttpStatusCode.TooManyRequests => "请求过于频繁或达到速率上限",
            HttpStatusCode.BadGateway => "上游模型暂时不可用或返回异常",
            HttpStatusCode.ServiceUnavailable => "当前没有可用的模型服务",
            _ => "接入商返回错误"
        };

    private static string FriendlyErrorType(string type) =>
        type.ToLowerInvariant() switch
        {
            "authentication" => "API Key 无效、已撤销或缺少",
            "permission_denied" => "API Key 权限不足或请求被安全策略拒绝",
            "payment_required" => "账户余额或额度不足",
            "rate_limit_exceeded" => "请求过于频繁或达到速率上限",
            "context_length_exceeded" => "上下文超过模型上限",
            "max_tokens_exceeded" => "请求的最大输出超过模型上限",
            "token_limit_exceeded" => "Token 预算或账户额度上限已达到",
            "model_not_found" => "模型不存在或当前 Key 无权使用",
            "provider_unavailable" => "上游模型暂时不可用",
            _ => string.Empty
        };

    private static string ReadRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            return $"；建议等待 {Math.Max(1, Math.Ceiling(delta.TotalSeconds)):0} 秒后重试";
        }

        if (retryAfter?.Date is { } date)
        {
            return $"；可在 {date.ToLocalTime():yyyy-MM-dd HH:mm:ss} 后重试";
        }

        return string.Empty;
    }

    private static string CleanProviderText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = string.Join(
            " ",
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= MaximumDisplayedErrorCharacters
            ? normalized
            : normalized[..MaximumDisplayedErrorCharacters] + "…";
    }

    private static async Task<byte[]> ReadLimitedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maximumBytes)
        {
            throw new InvalidDataException($"HTTP 响应超过 {maximumBytes} 字节安全上限。");
        }

        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            output.Write(buffer, 0, read);
            if (output.Length > maximumBytes)
            {
                throw new InvalidDataException($"HTTP 响应超过 {maximumBytes} 字节安全上限。");
            }
        }

        return output.ToArray();
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression =
                DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    private sealed record ProviderErrorDetail(string Message, string Type);
}
