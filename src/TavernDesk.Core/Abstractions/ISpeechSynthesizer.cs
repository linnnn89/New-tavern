namespace TavernDesk.Core.Abstractions;

public sealed record SpeechOptions
{
    public string ApiUrl { get; init; } = "https://api.fish.audio/v1/tts";
    public double Temperature { get; init; } = 0.7;
    public double TopP { get; init; } = 0.7;
    public double Volume { get; init; }
    public bool NormalizeLoudness { get; init; } = true;
    public bool Normalize { get; init; } = true;
    public int ChunkLength { get; init; } = 300;
    public string Latency { get; init; } = "normal";
    public int MaxNewTokens { get; init; } = 1024;
    public double RepetitionPenalty { get; init; } = 1.2;
    public int MinChunkLength { get; init; } = 50;
    public bool ConditionOnPreviousChunks { get; init; } = true;
    public double EarlyStopThreshold { get; init; } = 1;
    public bool QualityGuard { get; init; }
}

public sealed record SpeechRequest(string Text, string Model, string VoiceId, double Speed, string SecretReference,
    SpeechOptions? Options = null);

public interface ISpeechSynthesizer
{
    // Signed 16-bit little-endian mono PCM at 44100 Hz. Chunks may split samples.
    IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(SpeechRequest request, CancellationToken cancellationToken);
}

public sealed class SpeechException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
