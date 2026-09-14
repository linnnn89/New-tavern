using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using TavernDesk.App.Localization;
using TavernDesk.Core.Abstractions;
using TavernDesk.Infrastructure.Speech;

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
    public SpeechPlaybackService(ISpeechSynthesizer synthesizer, SpeechSettingsService settings, ISpeechAudioOutput output)
    {
        this.synthesizer = synthesizer; this.settings = settings; this.output = output;
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
        try
        {
            await _playGate.WaitAsync(cancellation.Token);
            entered = true;
            var config = await settings.LoadAsync(cancellation.Token);
            var text = PrepareText(displayText);
            if (text.Length == 0) throw new SpeechException("Empty");
            var request = new SpeechRequest(text, config.Model, config.VoiceFor(characterId), config.Speed, config.SecretReference, config.Options);
            if (string.IsNullOrWhiteSpace(request.VoiceId) || string.IsNullOrWhiteSpace(config.SecretReference))
                throw new SpeechException("NotConfigured");
            var cacheKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(request))));
            using var collected = new MemoryStream();
            async IAsyncEnumerable<ReadOnlyMemory<byte>> Audio()
            {
                var stream = _cacheKey == cacheKey && _cache is not null ? Cached(_cache) : synthesizer.SynthesizeAsync(request, cancellation.Token);
                await foreach (var chunk in stream.WithCancellation(cancellation.Token))
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (version != _version) yield break;
                    if (collected.Length + chunk.Length > 32 * 1024 * 1024) throw new SpeechException("TooLong");
                    collected.Write(chunk.Span);
                    if (Status != LanguageRuntime.GetString("Speech.Playing"))
                    {
                        Status = LanguageRuntime.GetString("Speech.Playing");
                        Changed?.Invoke(this, EventArgs.Empty);
                    }
                    yield return chunk;
                }
            }
            await output.PlayAsync(Audio(), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (version == _version)
            {
                _cache = collected.ToArray();
                _cacheKey = cacheKey;
                Status = LanguageRuntime.GetString("Speech.Finished");
            }
        }
        catch (OperationCanceledException)
        {
            if (version == _version) Status = LanguageRuntime.GetString(cancellation.IsCancellationRequested ? "Speech.Stopped" : "Speech.Timeout");
        }
        catch (Exception error)
        {
            if (version == _version) Status = LanguageRuntime.GetString("Speech." + (error is SpeechException speech ? speech.Code : "Failed"));
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
        return text.Replace("*", "").Replace("`", "").Trim();
    }
}
