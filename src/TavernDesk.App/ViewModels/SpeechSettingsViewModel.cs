using System.Globalization;
using System.Text.Json;
using TavernDesk.App.Localization;
using TavernDesk.App.Presentation;
using TavernDesk.Core.Abstractions;
using TavernDesk.Infrastructure.Speech;

namespace TavernDesk.App.ViewModels;

// Keep invalid numeric input visible until save; never silently retain an old numeric value.
public sealed class SpeechSettingsForm
{
    public string ApiUrl { get; set; } = "https://api.fish.audio/v1/tts";
    public string Model { get; set; } = "s2.1-pro-free";
    public string DefaultVoiceId { get; set; } = "";
    public string CharacterVoiceId { get; set; } = "";
    public string Speed { get; set; } = "1";
    public string Volume { get; set; } = "0";
    public string Temperature { get; set; } = (0.7).ToString(CultureInfo.CurrentCulture);
    public string TopP { get; set; } = (0.7).ToString(CultureInfo.CurrentCulture);
    public string ChunkLength { get; set; } = "300";
    public string MinChunkLength { get; set; } = "50";
    public string Latency { get; set; } = "normal";
    public string MaxNewTokens { get; set; } = "1024";
    public string RepetitionPenalty { get; set; } = (1.2).ToString(CultureInfo.CurrentCulture);
    public string EarlyStopThreshold { get; set; } = "1";
    public bool Normalize { get; set; } = true;
    public bool NormalizeLoudness { get; set; } = true;
    public bool ConditionOnPreviousChunks { get; set; } = true;
    public bool QualityGuard { get; set; }
    public bool ClearKey { get; set; }
}

public sealed class SpeechSettingsViewModel : ViewModelBase
{
    private readonly SpeechSettingsService _service;
    private readonly string? _characterId;
    private SpeechSettingsForm _form = new();
    private string? _baseline;
    private string _pendingApiKey = "";
    private string _status = "";
    private bool _hasSavedKey;
    private bool _isSaving;
    private string _modelSelection = "s2.1-pro-free";
    public SpeechSettingsViewModel(SpeechSettingsService service, string? characterId = null, string characterName = "")
    {
        _service = service; _characterId = characterId; CharacterName = characterName;
        SaveCommand = new AsyncRelayCommand(async () => { await SaveAsync(); });
        ReloadCommand = new AsyncRelayCommand(() => LoadAsync(true));
        RecommendedCommand = new RelayCommand(() =>
        {
            Form = new SpeechSettingsForm { DefaultVoiceId = Form.DefaultVoiceId, CharacterVoiceId = Form.CharacterVoiceId, ClearKey = Form.ClearKey };
            ModelSelection = Form.Model;
            Status = LanguageRuntime.GetString("Speech.DefaultsRestored");
        });
    }
    public SpeechSettingsForm Form { get => _form; private set => SetProperty(ref _form, value); }
    public IReadOnlyList<string> Models => [.. SpeechSettingsService.Models, LanguageRuntime.GetString("Speech.Custom")];
    public string ModelSelection
    {
        get => _modelSelection;
        set
        {
            if (value is null || !SetProperty(ref _modelSelection, value)) return;
            Form.Model = value == LanguageRuntime.GetString("Speech.Custom") ? "" : value;
            OnPropertyChanged(nameof(Form)); OnPropertyChanged(nameof(IsCustomModel));
        }
    }
    public bool IsCustomModel => _modelSelection == LanguageRuntime.GetString("Speech.Custom");
    public IReadOnlyList<string> Latencies { get; } = ["normal", "balanced", "low"];
    public bool IsCharacterEditor => _characterId is not null;
    public string CharacterName { get; }
    public string PendingApiKey { get => _pendingApiKey; set => SetProperty(ref _pendingApiKey, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool IsSaving { get => _isSaving; private set { if (SetProperty(ref _isSaving, value)) OnPropertyChanged(nameof(CanEdit)); } }
    public bool CanEdit => !IsSaving;
    public string KeyStatus => LanguageRuntime.GetString(_hasSavedKey ? "Speech.KeySaved" : "Speech.KeyMissing");
    public bool HasUnsavedChanges => _baseline is not null && (_baseline != JsonSerializer.Serialize(Form) || PendingApiKey.Length > 0);
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand ReloadCommand { get; }
    public RelayCommand RecommendedCommand { get; }
    public event EventHandler? Saved;

    public async Task LoadAsync(bool discard = false)
    {
        if (IsSaving || !discard && HasUnsavedChanges) return;
        try { SetForm(await _service.LoadAsync()); Status = ""; }
        catch { Status = LanguageRuntime.GetString("Speech.SettingsInvalid"); }
    }

    public void SetForm(SpeechSettings value)
    {
        static string N(double number) => number.ToString(CultureInfo.CurrentCulture);
        var o = value.Options;
        Form = new SpeechSettingsForm
        {
            ApiUrl = o.ApiUrl, Model = value.Model, DefaultVoiceId = value.DefaultVoiceId,
            CharacterVoiceId = _characterId is null ? "" : value.CharacterVoices.GetValueOrDefault(_characterId, ""),
            Speed = N(value.Speed), Volume = N(o.Volume), Temperature = N(o.Temperature), TopP = N(o.TopP),
            ChunkLength = N(o.ChunkLength), MinChunkLength = N(o.MinChunkLength), Latency = o.Latency,
            MaxNewTokens = N(o.MaxNewTokens), RepetitionPenalty = N(o.RepetitionPenalty), EarlyStopThreshold = N(o.EarlyStopThreshold),
            Normalize = o.Normalize, NormalizeLoudness = o.NormalizeLoudness,
            ConditionOnPreviousChunks = o.ConditionOnPreviousChunks, QualityGuard = o.QualityGuard
        };
        PendingApiKey = "";
        _modelSelection = SpeechSettingsService.Models.Contains(value.Model) ? value.Model : LanguageRuntime.GetString("Speech.Custom");
        OnPropertyChanged(nameof(ModelSelection)); OnPropertyChanged(nameof(IsCustomModel));
        _hasSavedKey = value.SecretReference.Length > 0;
        OnPropertyChanged(nameof(KeyStatus));
        _baseline = JsonSerializer.Serialize(Form);
    }

    public async Task<bool> SaveAsync()
    {
        if (IsSaving) return false;
        IsSaving = true;
        try
        {
            var f = Form;
            static double D(string text) => double.Parse(text, NumberStyles.Float, CultureInfo.CurrentCulture);
            static int I(string text) => int.Parse(text, NumberStyles.Integer, CultureInfo.CurrentCulture);
            var edited = new SpeechSettings
            {
                Model = f.Model, DefaultVoiceId = f.DefaultVoiceId, Speed = D(f.Speed),
                Options = new SpeechOptions
                {
                    ApiUrl = f.ApiUrl, Volume = D(f.Volume), Temperature = D(f.Temperature), TopP = D(f.TopP),
                    ChunkLength = I(f.ChunkLength), MinChunkLength = I(f.MinChunkLength), Latency = f.Latency,
                    MaxNewTokens = I(f.MaxNewTokens), RepetitionPenalty = D(f.RepetitionPenalty), EarlyStopThreshold = D(f.EarlyStopThreshold),
                    Normalize = f.Normalize, NormalizeLoudness = f.NormalizeLoudness,
                    ConditionOnPreviousChunks = f.ConditionOnPreviousChunks, QualityGuard = f.QualityGuard
                }
            };
            await _service.SaveAsync(edited, _characterId, f.CharacterVoiceId, PendingApiKey, f.ClearKey);
            SetForm(await _service.LoadAsync());
            Status = LanguageRuntime.GetString("Speech.SettingsSaved");
            Saved?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch { Status = LanguageRuntime.GetString("Speech.SettingsInvalid"); return false; }
        finally { IsSaving = false; }
    }
}
