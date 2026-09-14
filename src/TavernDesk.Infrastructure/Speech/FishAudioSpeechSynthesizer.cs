using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TavernDesk.Core.Abstractions;
using TavernDesk.Infrastructure.Diagnostics;

namespace TavernDesk.Infrastructure.Speech;

public sealed class FishAudioSpeechSynthesizer(ISecretStore secrets, HttpClient? client = null,
    ITavernDeskDiagnostics? diagnostics = null) : ISpeechSynthesizer
{
    private static readonly HttpClient SharedClient = new(new HttpClientHandler { AllowAutoRedirect = false })
    { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient _client = client ?? SharedClient;

    public IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(SpeechRequest request, CancellationToken cancellationToken)
        => StreamAsync(request, null, cancellationToken);

    internal IAsyncEnumerable<ReadOnlyMemory<byte>> TestAsync(SpeechRequest request, string key, CancellationToken cancellationToken)
        => StreamAsync(request, key, cancellationToken);

    private async IAsyncEnumerable<ReadOnlyMemory<byte>> StreamAsync(SpeechRequest request, string? keyOverride,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var log = new SpeechFailureLog(diagnostics ?? NullTavernDeskDiagnostics.Instance, request.Text);
        await using var iterator = SynthesizeCoreAsync(request, log, keyOverride, cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            bool next;
            try { next = await iterator.MoveNextAsync(); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) { log.Write(error); throw; }
            if (!next) yield break;
            yield return iterator.Current;
        }
    }

    private async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeCoreAsync(SpeechRequest request,
        SpeechFailureLog log, string? keyOverride, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var options = request.Options ?? new SpeechOptions();
        SpeechSettingsService.Validate(request.Model, request.Speed, options);
        if (string.IsNullOrWhiteSpace(request.VoiceId)) throw new SpeechException("NotConfigured");
        if (request.Text.Length == 0) throw new SpeechException("Empty");
        if (request.Text.Length > 12000) throw new SpeechException("TooLong");
        log.Context["stage"] = "credentials";
        var key = keyOverride ?? await secrets.ReadAsync(request.SecretReference, cancellationToken);
        log.Credential = key;
        if (string.IsNullOrWhiteSpace(key)) throw new SpeechException("NotConfigured");
        using var message = new HttpRequestMessage(HttpMethod.Post, options.ApiUrl);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        message.Headers.Add("model", request.Model);
        var body = new
        {
            text = request.Text, reference_id = request.VoiceId, format = "pcm", sample_rate = 44100,
            latency = options.Latency,
            prosody = new { speed = request.Speed, volume = options.Volume, normalize_loudness = options.NormalizeLoudness },
            temperature = options.Temperature, top_p = options.TopP, normalize = options.Normalize,
            chunk_length = options.ChunkLength, min_chunk_length = options.MinChunkLength,
            max_new_tokens = options.MaxNewTokens, repetition_penalty = options.RepetitionPenalty,
            condition_on_previous_chunks = options.ConditionOnPreviousChunks, early_stop_threshold = options.EarlyStopThreshold,
            features = options.QualityGuard ? new[] { "quality-guard" } : Array.Empty<string>()
        };
        message.Content = JsonContent.Create(body);
        var requestStructure = JsonSerializer.SerializeToNode(body)!.AsObject();
        requestStructure["text"] = "[STORY_OMITTED]";
        requestStructure["reference_id"] = "[OMITTED]";
        log.Context["request_structure"] = requestStructure;
        log.Context["model"] = log.Sanitize(request.Model);
        log.Context["method"] = "POST";
        // Do not log custom URL paths, queries, or user info, which can contain secrets.
        log.Context["endpoint_origin"] = log.Sanitize(new Uri(options.ApiUrl).GetLeftPart(UriPartial.Authority));
        log.Context["stage"] = "http_headers";
        using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        headerTimeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token);
        log.Context["http_status"] = (int)response.StatusCode;
        log.Context["response_media_type"] = response.Content.Headers.ContentType?.MediaType;
        if (!response.IsSuccessStatusCode)
        {
            log.Context["stage"] = "provider_response";
            await ReadErrorAsync(response, log, cancellationToken);
            throw new SpeechException(((int)response.StatusCode) switch
            {
                401 or 403 => "Authentication", 402 => "Credits", 429 => "RateLimit", _ => "ProviderError"
            });
        }
        var type = response.Content.Headers.ContentType?.MediaType;
        if (type is not null && (type.Contains("json") || type.StartsWith("text/")))
        {
            log.Context["stage"] = "response_format";
            await ReadErrorAsync(response, log, cancellationToken);
            throw new SpeechException("InvalidAudio");
        }
        log.Context["stage"] = "audio_stream";
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var bytes = new byte[8192];
        var total = 0;
        while (true)
        {
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readTimeout.CancelAfter(TimeSpan.FromSeconds(60));
            var count = await stream.ReadAsync(bytes, readTimeout.Token);
            if (count == 0) break;
            total += count;
            log.Context["audio_bytes_received"] = total;
            if (total > 32 * 1024 * 1024) throw new SpeechException("TooLong");
            yield return bytes.AsMemory(0, count).ToArray();
        }
        if (total == 0 || total % 2 != 0) throw new SpeechException("InvalidAudio");
    }

    private static async Task ReadErrorAsync(HttpResponseMessage response, SpeechFailureLog log,
        CancellationToken cancellationToken)
    {
        // Bound both memory and time. Diagnostic reads must not replace the
        // original provider failure with a second error or an indefinite wait.
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var bytes = new byte[16 * 1024 + 1];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(count), timeout.Token);
                if (read == 0) break;
                count += read;
            }
            if (count == bytes.Length)
            {
                log.Context["response_error"] = "[OVERSIZED_BODY_OMITTED]";
                return;
            }
            var raw = Encoding.UTF8.GetString(bytes, 0, count);
            try { log.Context["response_error"] = log.SanitizeResponse(JsonNode.Parse(raw)); }
            catch (JsonException)
            {
                // Malformed JSON/HTML can contain escaped or partial request bodies.
                log.Context["response_error"] = "[NON_JSON_BODY_OMITTED]";
            }
        }
        catch (Exception error)
        {
            log.Context["response_read_error_type"] = error.GetType().FullName;
        }
    }
}
