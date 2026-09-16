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
    private SpeechSettings? _loadedSettings;
    private bool _restoreRecommended;
    private string _pendingApiKey = "";
    private string _status = "";
    private string? _validationField;
    private string _validationMessage = "";
    private bool _hasSavedKey;
    private bool _isSaving;
    private CancellationTokenSource? _testCancellation;
    private string _modelSelection = "s2.1-pro-free";
    public SpeechSettingsViewModel(SpeechSettingsService service, string? characterId = null, string characterName = "")
    {
        _service = service; _characterId = characterId; CharacterName = characterName;
        SaveCommand = new AsyncRelayCommand(async () => { await SaveAsync(); });
        ReloadCommand = new AsyncRelayCommand(() => LoadAsync(true));
        TestCommand = new AsyncRelayCommand(TestConnectionAsync);
        CancelTestCommand = new RelayCommand(CancelTest);
        RefreshCacheCommand = new AsyncRelayCommand(RefreshCacheUsageAsync);
        RecommendedCommand = new RelayCommand(() =>
        {
            _restoreRecommended = true;
            Form = new SpeechSettingsForm { DefaultVoiceId = Form.DefaultVoiceId, CharacterVoiceId = Form.CharacterVoiceId, ClearKey = Form.ClearKey };
            ModelSelection = Form.Model;
            ClearValidation();
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
    public string? ValidationField { get => _validationField; private set => SetProperty(ref _validationField, value); }
    public string ValidationMessage { get => _validationMessage; private set => SetProperty(ref _validationMessage, value); }
    public bool IsSaving { get => _isSaving; private set { if (SetProperty(ref _isSaving, value)) OnPropertyChanged(nameof(CanEdit)); } }
    public bool IsTesting => _testCancellation is not null;
    public bool CanEdit => !IsSaving && !IsTesting;
    public bool HasSaveWarning { get; private set; }
    public string KeyStatus => LanguageRuntime.GetString(_hasSavedKey ? "Speech.KeySaved" : "Speech.KeyMissing");
    public bool HasUnsavedChanges => _restoreRecommended || PendingApiKey.Length > 0 || _baseline is not null && _baseline != JsonSerializer.Serialize(Form);
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand ReloadCommand { get; }
    public RelayCommand RecommendedCommand { get; }
    public AsyncRelayCommand TestCommand { get; }
    public RelayCommand CancelTestCommand { get; }
    public AsyncRelayCommand RefreshCacheCommand { get; }
    public string CacheDirectory => _service.AudioCache?.DirectoryPath ?? "";
    private string _cacheUsage = "";
    public string CacheUsage { get => _cacheUsage; private set => SetProperty(ref _cacheUsage, value); }
    private async Task RefreshCacheUsageAsync()
    {
        var usage = _service.AudioCache is { } cache ? await cache.GetUsageAsync() : null;
        CacheUsage = usage is { Available: true }
            ? LanguageRuntime.Format("Speech.CacheUsage", usage.Bytes / 1048576d, usage.Files)
            : LanguageRuntime.GetString("Speech.CacheUnavailable");
    }
    public event EventHandler? Saved;
    public event EventHandler? ValidationFailed;

    public async Task LoadAsync(bool discard = false)
    {
        await RefreshCacheUsageAsync();
        if (!CanEdit || !discard && HasUnsavedChanges) return;
        try { SetForm(await _service.LoadAsync()); Status = ""; }
        catch (Exception error) { Status = ErrorText(error, "SettingsReadFailed"); }
    }

    public void SetForm(SpeechSettings value)
    {
        _restoreRecommended = false;
        ClearValidation();
        _loadedSettings = new SpeechSettings
        {
            Model = value.Model, DefaultVoiceId = value.DefaultVoiceId, Speed = value.Speed,
            SecretReference = value.SecretReference, Options = value.Options,
            CharacterVoices = new(value.CharacterVoices, StringComparer.Ordinal)
        };
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
        if (!CanEdit) return false;
        IsSaving = true;
        HasSaveWarning = false;
        ClearValidation();
        try
        {
            if (_loadedSettings is null) throw new SpeechException("SettingsReadFailed");
            var f = Form;
            var edited = ReadEditedSettings();
            var result = await _service.SaveAsync(edited, _characterId, f.CharacterVoiceId, PendingApiKey, f.ClearKey, _loadedSettings, _restoreRecommended);
            SetForm(result.Settings);
            HasSaveWarning = result.CleanupPending;
            Status = LanguageRuntime.GetString(HasSaveWarning ? "Speech.SettingsSavedCleanupPending" : "Speech.SettingsSaved");
            Saved?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception error)
        {
            Status = ErrorText(error, "SettingsWriteFailed");
            ValidationField = error is SpeechException speech ? speech.Field switch
            {
                "DefaultVoice" => nameof(SpeechSettingsForm.DefaultVoiceId),
                "CharacterVoice" => nameof(SpeechSettingsForm.CharacterVoiceId),
                { } field => field,
                _ => speech.Code switch
                {
                    "ModelInvalid" => nameof(SpeechSettingsForm.Model),
                    "ApiUrlInvalid" or "ConnectionChanged" => nameof(SpeechSettingsForm.ApiUrl),
                    "LatencyInvalid" => nameof(SpeechSettingsForm.Latency),
                    "KeyConflict" or "KeySaveFailed" => "ApiKey",
                    _ => null
                }
            } : null;
            ValidationMessage = ValidationField is null ? "" : Status;
            return false;
        }
        finally
        {
            IsSaving = false;
            if (ValidationField is not null) ValidationFailed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void CancelTest() => _testCancellation?.Cancel();

    public async Task TestConnectionAsync()
    {
        if (!CanEdit) return;
        using var cancellation = new CancellationTokenSource();
        _testCancellation = cancellation;
        OnPropertyChanged(nameof(IsTesting)); OnPropertyChanged(nameof(CanEdit));
        Status = LanguageRuntime.GetString("Speech.Testing");
        ClearValidation();
        try
        {
            if (_loadedSettings is null) throw new SpeechException("SettingsReadFailed");
            var edited = ReadEditedSettings();
            var voice = IsCharacterEditor && !string.IsNullOrWhiteSpace(Form.CharacterVoiceId)
                ? Form.CharacterVoiceId : Form.DefaultVoiceId;
            var bytes = await _service.TestConnectionAsync(edited, voice, PendingApiKey, Form.ClearKey, _loadedSettings, cancellation.Token);
            Status = LanguageRuntime.Format("Speech.TestSucceeded", bytes);
        }
        catch (OperationCanceledException)
        {
            Status = LanguageRuntime.GetString(cancellation.IsCancellationRequested ? "Speech.Stopped" : "Speech.Timeout");
        }
        catch (Exception error)
        {
            Status = ErrorText(error, "Failed") + " " + LanguageRuntime.GetString("Speech.TestFailureLog");
        }
        finally
        {
            _testCancellation = null;
            OnPropertyChanged(nameof(IsTesting)); OnPropertyChanged(nameof(CanEdit));
        }
    }

    private SpeechSettings ReadEditedSettings()
    {
        var f = Form;
        static double D(string text, string field) => double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) && double.IsFinite(value)
            ? value : throw new SpeechException("InvalidField", field);
        static int I(string text, string field) => int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var value)
            ? value : throw new SpeechException("InvalidField", field);
        return new SpeechSettings
        {
            Model = f.Model, DefaultVoiceId = f.DefaultVoiceId, Speed = D(f.Speed, "Speed"),
            Options = new SpeechOptions
            {
                ApiUrl = f.ApiUrl, Volume = D(f.Volume, "Volume"), Temperature = D(f.Temperature, "Temperature"), TopP = D(f.TopP, "TopP"),
                ChunkLength = I(f.ChunkLength, "ChunkLength"), MinChunkLength = I(f.MinChunkLength, "MinChunkLength"), Latency = f.Latency,
                MaxNewTokens = I(f.MaxNewTokens, "MaxNewTokens"), RepetitionPenalty = D(f.RepetitionPenalty, "RepetitionPenalty"),
                EarlyStopThreshold = D(f.EarlyStopThreshold, "EarlyStopThreshold"),
                Normalize = f.Normalize, NormalizeLoudness = f.NormalizeLoudness,
                ConditionOnPreviousChunks = f.ConditionOnPreviousChunks, QualityGuard = f.QualityGuard
            }
        };
    }

    private void ClearValidation()
    {
        ValidationField = null;
        ValidationMessage = "";
    }

    private static string ErrorText(Exception error, string fallback) => error is SpeechException speech
        ? speech.Field is { } field
            ? LanguageRuntime.Format("Speech." + speech.Code, LanguageRuntime.GetString("Speech." + field))
            : LanguageRuntime.GetString("Speech." + speech.Code)
        : LanguageRuntime.GetString("Speech." + fallback);
}
