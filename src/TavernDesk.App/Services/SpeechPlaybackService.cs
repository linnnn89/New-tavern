using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using TavernDesk.App.Localization;
using TavernDesk.Core.Abstractions;
using TavernDesk.Infrastructure.Speech;
using TavernDesk.Infrastructure.Diagnostics;

namespace TavernDesk.App.Services;

public interface ISpeechAudioOutput
{
    Task PlayAsync(IAsyncEnumerable<ReadOnlyMemory<byte>> audio, CancellationToken cancellationToken);
}

public sealed class SpeechPlaybackService
{
    private readonly ISpeechSynthesizer synthesizer;
    private readonly SpeechSettingsService settings;
    private readonly ISpeechAudioOutput output;
    private readonly ITavernDeskDiagnostics diagnostics;
    private readonly SpeechAudioCache? audioCache;
    public SpeechPlaybackService(ISpeechSynthesizer synthesizer, SpeechSettingsService settings, ISpeechAudioOutput output,
        ITavernDeskDiagnostics? diagnostics = null, SpeechAudioCache? audioCache = null)
    {
        this.synthesizer = synthesizer; this.settings = settings; this.output = output;
        this.diagnostics = diagnostics ?? NullTavernDeskDiagnostics.Instance;
        this.audioCache = audioCache;
        settings.Saved += (_, _) => Stop();
    }
    private readonly SemaphoreSlim _playGate = new(1, 1);
    private CancellationTokenSource? _current;
    private long _version;
    private string? _cacheKey;
    private byte[]? _cache;
    public string? MessageKey { get; private set; }
    public bool IsActive { get; private set; }
    public string Status { get; private set; } = "";
    public event EventHandler? Changed;

    public void StopConversation(string conversationId)
    {
        if (MessageKey?.StartsWith(conversationId + ":", StringComparison.Ordinal) == true) Stop();
    }

    public void Stop(string? messageKey = null)
    {
        if (messageKey is not null && MessageKey != messageKey) return;
        ++_version;
        _current?.Cancel();
        _current = null;
        IsActive = false;
        Status = LanguageRuntime.GetString("Speech.Stopped");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task ToggleAsync(string messageKey, string characterId, string displayText)
    {
        if (IsActive && MessageKey == messageKey) { Stop(); return; }
        Stop();
        var version = _version;
        using var cancellation = new CancellationTokenSource();
        _current = cancellation;
        MessageKey = messageKey;
        IsActive = true;
        Status = LanguageRuntime.GetString("Speech.Preparing");
        Changed?.Invoke(this, EventArgs.Empty);
        var entered = false;
        var failureLog = new SpeechFailureLog(diagnostics, displayText);
        failureLog.Context["stage"] = "settings";
        try
        {
            await _playGate.WaitAsync(cancellation.Token);
            entered = true;
            var config = await settings.LoadAsync(cancellation.Token);
            var text = PrepareText(displayText);
            failureLog = new SpeechFailureLog(diagnostics, text);
            failureLog.Context["stage"] = "request_preparation";
            if (text.Length == 0) throw new SpeechException("Empty");
            var request = new SpeechRequest(text, config.Model, config.VoiceFor(characterId), config.Speed, config.SecretReference, config.Options);
            if (string.IsNullOrWhiteSpace(request.VoiceId) || string.IsNullOrWhiteSpace(config.SecretReference))
                throw new SpeechException("NotConfigured");
            var cacheKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(request))));
            var cachedAudio = audioCache is not null
                ? await audioCache.TryReadAsync(cacheKey, cancellation.Token)
                : _cacheKey == cacheKey ? _cache : null;
            using var collected = new MemoryStream();
            async IAsyncEnumerable<ReadOnlyMemory<byte>> Audio()
            {
                var stream = cachedAudio is not null ? Cached(cachedAudio) : synthesizer.SynthesizeAsync(request, cancellation.Token);
                await foreach (var chunk in stream.WithCancellation(cancellation.Token))
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (version != _version) yield break;
                    if (collected.Length + chunk.Length > 32 * 1024 * 1024) throw new SpeechException("TooLong");
                    failureLog.Context["stage"] = "audio_playback";
                    collected.Write(chunk.Span);
                    failureLog.Context["audio_bytes_received"] = collected.Length;
                    if (Status != LanguageRuntime.GetString("Speech.Playing"))
                    {
                        Status = LanguageRuntime.GetString("Speech.Playing");
                        Changed?.Invoke(this, EventArgs.Empty);
                    }
                    yield return chunk;
                }
            }
            failureLog.Context["stage"] = "audio_output_initialization";
            await output.PlayAsync(Audio(), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (version == _version)
            {
                if (audioCache is not null)
                {
                    if (cachedAudio is null) await audioCache.StoreAsync(cacheKey, collected.ToArray(), cancellation.Token);
                }
                else { _cache = collected.ToArray(); _cacheKey = cacheKey; }
                if (version == _version) Status = LanguageRuntime.GetString("Speech.Finished");
            }
        }
        catch (OperationCanceledException error)
        {
            if (!cancellation.IsCancellationRequested) failureLog.Write(error);
            if (version == _version) Status = LanguageRuntime.GetString(cancellation.IsCancellationRequested ? "Speech.Stopped" : "Speech.Timeout");
        }
        catch (Exception error)
        {
            failureLog.Write(error);
            if (version == _version)
                Status = error is SpeechException { Field: { } field } fieldError
                    ? LanguageRuntime.Format("Speech." + fieldError.Code, LanguageRuntime.GetString("Speech." + field))
                    : LanguageRuntime.GetString("Speech." + (error is SpeechException speech ? speech.Code : "Failed"));
        }
        finally
        {
            if (entered) _playGate.Release();
            if (version == _version)
            {
                IsActive = false;
                _current = null;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> Cached(byte[] bytes)
    {
        for (var offset = 0; offset < bytes.Length; offset += 8192)
            yield return bytes.AsMemory(offset, Math.Min(8192, bytes.Length - offset));
        await Task.CompletedTask;
    }

    public static string PrepareText(string text)
    {
        // Only transform a copy; persisted messages and prompts stay intact.
        var timeout = TimeSpan.FromMilliseconds(200);
        text = Regex.Replace(text, @"(?ms)^\s*(```|~~~).*?(?:^\s*\1[^\r\n]*(?:\r?\n|$)|\z)", "", RegexOptions.None, timeout);
        text = Regex.Replace(text, @"!\[[^\]]*\]\([^)]*\)", "", RegexOptions.None, timeout);
        text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]*\)", "$1", RegexOptions.None, timeout);
        // The chat renderer treats angle brackets as visible text, not HTML.
        text = Regex.Replace(text, @"<\|[^>]*\|>", "", RegexOptions.None, timeout);
        text = Regex.Replace(text, @"(?m)^\s{0,3}#{1,6}\s+", "", RegexOptions.None, timeout);
        // Match the presenter's paired bold/code syntax in one pass, keeping literals inside inline code intact.
        return Regex.Replace(text, @"`([^`\r\n]*)`|\*\*([^\r\n]*?)\*\*",
            match => match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value,
            RegexOptions.None, timeout).Trim();
    }
}
