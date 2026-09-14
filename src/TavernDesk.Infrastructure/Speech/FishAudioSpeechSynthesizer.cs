using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using TavernDesk.Core.Abstractions;

namespace TavernDesk.Infrastructure.Speech;

public sealed class FishAudioSpeechSynthesizer(ISecretStore secrets, HttpClient? client = null) : ISpeechSynthesizer
{
    private static readonly HttpClient SharedClient = new(new HttpClientHandler { AllowAutoRedirect = false })
    { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient _client = client ?? SharedClient;

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(SpeechRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var options = request.Options ?? new SpeechOptions();
        SpeechSettingsService.Validate(request.Model, request.Speed, options);
        if (string.IsNullOrWhiteSpace(request.VoiceId)) throw new SpeechException("NotConfigured");
        if (request.Text.Length == 0) throw new SpeechException("Empty");
        if (request.Text.Length > 12000) throw new SpeechException("TooLong");
        var key = await secrets.ReadAsync(request.SecretReference, cancellationToken);
        if (string.IsNullOrWhiteSpace(key)) throw new SpeechException("NotConfigured");
        using var message = new HttpRequestMessage(HttpMethod.Post, options.ApiUrl);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        message.Headers.Add("model", request.Model);
        message.Content = JsonContent.Create(new
        {
            text = request.Text, reference_id = request.VoiceId, format = "pcm", sample_rate = 44100,
            latency = options.Latency,
            prosody = new { speed = request.Speed, volume = options.Volume, normalize_loudness = options.NormalizeLoudness },
            temperature = options.Temperature, top_p = options.TopP, normalize = options.Normalize,
            chunk_length = options.ChunkLength, min_chunk_length = options.MinChunkLength,
            max_new_tokens = options.MaxNewTokens, repetition_penalty = options.RepetitionPenalty,
            condition_on_previous_chunks = options.ConditionOnPreviousChunks, early_stop_threshold = options.EarlyStopThreshold,
            features = options.QualityGuard ? new[] { "quality-guard" } : Array.Empty<string>()
        });
        using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        headerTimeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token);
        if (!response.IsSuccessStatusCode)
            throw new SpeechException(((int)response.StatusCode) switch
            {
                401 or 403 => "Authentication", 402 => "Credits", 429 => "RateLimit", _ => "ProviderError"
            });
        var type = response.Content.Headers.ContentType?.MediaType;
        if (type is not null && (type.Contains("json") || type.StartsWith("text/")))
            throw new SpeechException("InvalidAudio");
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
            if (total > 32 * 1024 * 1024) throw new SpeechException("TooLong");
            yield return bytes.AsMemory(0, count).ToArray();
        }
        if (total == 0 || total % 2 != 0) throw new SpeechException("InvalidAudio");
    }
}
