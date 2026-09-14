using System.Text.Json;
using TavernDesk.Core.Abstractions;

namespace TavernDesk.Infrastructure.Speech;

public sealed class SpeechSettings
{
    public string Model { get; set; } = "s2.1-pro-free";
    public string DefaultVoiceId { get; set; } = "";
    public double Speed { get; set; } = 1;
    public string SecretReference { get; set; } = "";
    public SpeechOptions Options { get; set; } = new();
    public Dictionary<string, string> CharacterVoices { get; set; } = new(StringComparer.Ordinal);
    public string VoiceFor(string characterId) => CharacterVoices.GetValueOrDefault(characterId) is { Length: > 0 } voice ? voice : DefaultVoiceId;
}

public sealed class SpeechSettingsService(IAppSettingsRepository settings, ISecretStore secrets)
{
    private const string Key = "speech.fish.v1";
    private readonly SemaphoreSlim _gate = new(1, 1);
    public static IReadOnlyList<string> Models { get; } = ["s2.1-pro", "s2.1-pro-free", "s2-pro", "s1", "drama-3-preview"];
    public event EventHandler? Saved;

    public static void Validate(string model, double speed, SpeechOptions options)
    {
        static bool Between(double value, double min, double max) => double.IsFinite(value) && value >= min && value <= max;
        if (string.IsNullOrWhiteSpace(model) || model.Length > 128 || model.Any(c => c <= 32 || c >= 127) || !Between(speed, 0.5, 2)
            || !Uri.TryCreate(options.ApiUrl, UriKind.Absolute, out var uri)
            || !(uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.Query.Length != 0
            || !Between(options.Temperature, 0, 1) || !Between(options.TopP, 0, 1)
            || !Between(options.Volume, -20, 20) || options.ChunkLength is < 100 or > 300
            || options.MinChunkLength is < 0 or > 100 || options.MaxNewTokens <= 0
            || !double.IsFinite(options.RepetitionPenalty) || options.RepetitionPenalty <= 0
            || !Between(options.EarlyStopThreshold, 0, 1)
            || options.Latency is not ("normal" or "balanced" or "low"))
            throw new SpeechException("SettingsInvalid");
    }

    public async Task<SpeechSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var json = await settings.GetAsync(Key, cancellationToken);
        var result = json is null ? new SpeechSettings() : JsonSerializer.Deserialize<SpeechSettings>(json) ?? throw new SpeechException("SettingsInvalid");
        if (result.Options is null || result.CharacterVoices is null || result.DefaultVoiceId is null || result.SecretReference is null)
            throw new SpeechException("SettingsInvalid");
        return result;
    }

    public Task SaveAsync(string model, string defaultVoice, double speed, string characterId, string characterVoice, string newKey) =>
        UpdateAsync(current => { current.Model = model; current.DefaultVoiceId = defaultVoice.Trim(); current.Speed = speed;
            SetVoice(current, characterId, characterVoice); }, newKey, false);

    public Task SaveAsync(SpeechSettings edited, string? characterId, string characterVoice, string newKey, bool clearKey = false) =>
        UpdateAsync(current =>
        {
            current.Model = edited.Model;
            current.DefaultVoiceId = edited.DefaultVoiceId.Trim();
            current.Speed = edited.Speed;
            current.Options = edited.Options with { ApiUrl = edited.Options.ApiUrl.Trim() };
            // Merge only the edited role into the latest map; other windows may have saved another role.
            if (characterId is not null) SetVoice(current, characterId, characterVoice);
        }, newKey, clearKey);

    private static void SetVoice(SpeechSettings current, string characterId, string voice)
    {
        if (voice.Length > 128) throw new SpeechException("SettingsInvalid");
        if (string.IsNullOrWhiteSpace(voice)) current.CharacterVoices.Remove(characterId);
        else current.CharacterVoices[characterId] = voice.Trim();
    }

    private async Task UpdateAsync(Action<SpeechSettings> update, string newKey, bool clearKey)
    {
        if (clearKey && !string.IsNullOrWhiteSpace(newKey))
            throw new SpeechException("SettingsInvalid");
        await _gate.WaitAsync();
        try
        {
            var current = await LoadAsync();
            var oldReference = current.SecretReference;
            update(current);
            Validate(current.Model, current.Speed, current.Options);
            if (current.DefaultVoiceId.Length > 128) throw new SpeechException("SettingsInvalid");
            if (clearKey) current.SecretReference = "";
            if (!string.IsNullOrWhiteSpace(newKey)) current.SecretReference = await secrets.SaveAsync("fish-audio", newKey.Trim());
            try { await settings.SetAsync(Key, JsonSerializer.Serialize(current)); }
            catch
            {
                if (current.SecretReference != oldReference) await secrets.DeleteAsync(current.SecretReference);
                throw;
            }
            if (oldReference.Length > 0 && current.SecretReference != oldReference)
                await secrets.DeleteAsync(oldReference);
        }
        finally { _gate.Release(); }
        Saved?.Invoke(this, EventArgs.Empty);
    }
}
