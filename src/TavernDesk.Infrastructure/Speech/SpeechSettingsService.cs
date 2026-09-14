using System.Text.Json;
using TavernDesk.Core.Abstractions;
using TavernDesk.Infrastructure.Diagnostics;

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

public sealed record SpeechSettingsSaveResult(SpeechSettings Settings, bool CleanupPending);

public sealed class SpeechSettingsService(IAppSettingsRepository settings, ISecretStore secrets, ITavernDeskDiagnostics? diagnostics = null)
{
    private const string Key = "speech.fish.v1";
    private readonly SemaphoreSlim _gate = new(1, 1);
    public static IReadOnlyList<string> Models { get; } = ["s2.1-pro", "s2.1-pro-free", "s2-pro", "s1", "drama-3-preview"];
    public event EventHandler? Saved;

    public static void Validate(string model, double speed, SpeechOptions options)
    {
        static bool Between(double value, double min, double max) => double.IsFinite(value) && value >= min && value <= max;
        if (string.IsNullOrWhiteSpace(model) || model.Length > 128 || model.Any(c => c <= 32 || c >= 127))
            throw new SpeechException("ModelInvalid");
        if (!Uri.TryCreate(options.ApiUrl, UriKind.Absolute, out var uri)
            || !(uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.Query.Length != 0)
            throw new SpeechException("ApiUrlInvalid");
        static void Require(bool valid, string field) { if (!valid) throw new SpeechException("InvalidField", field); }
        Require(Between(speed, 0.5, 2), "Speed");
        Require(Between(options.Temperature, 0, 1), "Temperature");
        Require(Between(options.TopP, 0, 1), "TopP");
        Require(Between(options.Volume, -20, 20), "Volume");
        Require(options.ChunkLength is >= 100 and <= 300, "ChunkLength");
        Require(options.MinChunkLength is >= 0 and <= 100, "MinChunkLength");
        Require(options.MaxNewTokens > 0, "MaxNewTokens");
        Require(double.IsFinite(options.RepetitionPenalty) && options.RepetitionPenalty > 0, "RepetitionPenalty");
        Require(Between(options.EarlyStopThreshold, 0, 1), "EarlyStopThreshold");
        if (options.Latency is not ("normal" or "balanced" or "low")) throw new SpeechException("LatencyInvalid");
    }

    public async Task<SpeechSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var json = await settings.GetAsync(Key, cancellationToken);
            var result = json is null ? new SpeechSettings() : JsonSerializer.Deserialize<SpeechSettings>(json) ?? throw new SpeechException("SettingsInvalid");
            if (result.Options is null || result.CharacterVoices is null || result.DefaultVoiceId is null || result.SecretReference is null)
                throw new SpeechException("SettingsInvalid");
            return result;
        }
        catch (SpeechException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            LogFailure("SettingsRead", error);
            throw new SpeechException(error is JsonException ? "SettingsInvalid" : "SettingsReadFailed");
        }
    }

    public Task SaveAsync(string model, string defaultVoice, double speed, string characterId, string characterVoice, string newKey) =>
        UpdateAsync(current => { current.Model = model; current.DefaultVoiceId = defaultVoice.Trim(); current.Speed = speed;
            SetVoice(current, characterId, characterVoice); }, newKey, false);

    public Task<SpeechSettingsSaveResult> SaveAsync(SpeechSettings edited, string? characterId, string characterVoice, string newKey,
        bool clearKey = false, SpeechSettings? baseline = null) =>
        UpdateAsync(current =>
        {
            // A form owns only fields changed since it was loaded, including fields in Options.
            // Compare and merge under the same gate as the write so stale role windows cannot restore a paid model.
            T Merge<T>(T editedValue, T original, T latest) => baseline is null || !EqualityComparer<T>.Default.Equals(editedValue, original) ? editedValue : latest;
            var original = baseline ?? edited;
            current.Model = Merge(edited.Model, original.Model, current.Model);
            current.DefaultVoiceId = Merge(edited.DefaultVoiceId.Trim(), original.DefaultVoiceId, current.DefaultVoiceId);
            current.Speed = Merge(edited.Speed, original.Speed, current.Speed);
            var e = edited.Options; var b = original.Options; var c = current.Options;
            current.Options = c with
            {
                ApiUrl = Merge(e.ApiUrl.Trim(), b.ApiUrl, c.ApiUrl),
                Temperature = Merge(e.Temperature, b.Temperature, c.Temperature), TopP = Merge(e.TopP, b.TopP, c.TopP),
                Volume = Merge(e.Volume, b.Volume, c.Volume), NormalizeLoudness = Merge(e.NormalizeLoudness, b.NormalizeLoudness, c.NormalizeLoudness),
                Normalize = Merge(e.Normalize, b.Normalize, c.Normalize), ChunkLength = Merge(e.ChunkLength, b.ChunkLength, c.ChunkLength),
                Latency = Merge(e.Latency, b.Latency, c.Latency), MaxNewTokens = Merge(e.MaxNewTokens, b.MaxNewTokens, c.MaxNewTokens),
                RepetitionPenalty = Merge(e.RepetitionPenalty, b.RepetitionPenalty, c.RepetitionPenalty),
                MinChunkLength = Merge(e.MinChunkLength, b.MinChunkLength, c.MinChunkLength),
                ConditionOnPreviousChunks = Merge(e.ConditionOnPreviousChunks, b.ConditionOnPreviousChunks, c.ConditionOnPreviousChunks),
                EarlyStopThreshold = Merge(e.EarlyStopThreshold, b.EarlyStopThreshold, c.EarlyStopThreshold),
                QualityGuard = Merge(e.QualityGuard, b.QualityGuard, c.QualityGuard)
            };
            if (characterId is not null && (baseline is null || characterVoice.Trim() != baseline.CharacterVoices.GetValueOrDefault(characterId, "")))
                SetVoice(current, characterId, characterVoice);
        }, newKey, clearKey);

    private static void SetVoice(SpeechSettings current, string characterId, string voice)
    {
        if (voice.Length > 128) throw new SpeechException("VoiceInvalid", "CharacterVoice");
        if (string.IsNullOrWhiteSpace(voice)) current.CharacterVoices.Remove(characterId);
        else current.CharacterVoices[characterId] = voice.Trim();
    }

    private async Task<SpeechSettingsSaveResult> UpdateAsync(Action<SpeechSettings> update, string newKey, bool clearKey)
    {
        if (clearKey && !string.IsNullOrWhiteSpace(newKey))
            throw new SpeechException("KeyConflict");
        SpeechSettingsSaveResult result;
        await _gate.WaitAsync();
        try
        {
            var current = await LoadAsync();
            var oldReference = current.SecretReference;
            update(current);
            Validate(current.Model, current.Speed, current.Options);
            if (current.DefaultVoiceId.Length > 128) throw new SpeechException("VoiceInvalid", "DefaultVoice");
            if (clearKey) current.SecretReference = "";
            if (!string.IsNullOrWhiteSpace(newKey))
            {
                try { current.SecretReference = await secrets.SaveAsync("fish-audio", newKey.Trim()); }
                catch (Exception error) { LogFailure("KeySave", error); throw new SpeechException("KeySaveFailed"); }
            }
            try { await settings.SetAsync(Key, JsonSerializer.Serialize(current)); }
            catch (Exception error)
            {
                LogFailure("SettingsWrite", error);
                if (current.SecretReference.Length > 0 && current.SecretReference != oldReference)
                    await TryDeleteSecretAsync(current.SecretReference);
                throw new SpeechException("SettingsWriteFailed");
            }
            var cleanupPending = false;
            if (oldReference.Length > 0 && current.SecretReference != oldReference)
                cleanupPending = !await TryDeleteSecretAsync(oldReference);
            // The database commit is the success boundary. Cleanup failure must not undo UI state or suppress cancellation.
            result = new(current, cleanupPending);
        }
        finally { _gate.Release(); }
        try { Saved?.Invoke(this, EventArgs.Empty); }
        catch (Exception error) { LogFailure("SettingsNotification", error); }
        return result;
    }

    private async Task<bool> TryDeleteSecretAsync(string reference)
    {
        try { await secrets.DeleteAsync(reference); return true; }
        catch (Exception error) { LogFailure("KeyCleanup", error); return false; }
    }

    private void LogFailure(string category, Exception error)
    {
        try { diagnostics?.LogError("Speech." + category, error); }
        catch { /* Diagnostics cannot change a committed save result. */ }
    }
}
