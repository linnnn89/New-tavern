using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows.Media;
using TavernDesk.App.Localization;
using TavernDesk.App.Presentation;
using TavernDesk.App.Services;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;
using TavernDesk.Infrastructure.Diagnostics;
using TavernDesk.Infrastructure.Storage;

namespace TavernDesk.App.ViewModels;

public sealed class ProviderSettingsViewModel : ViewModelBase
{
    public const string ChatAutoScrollSettingKey = "ui.chat.autoScroll";
    public const string InterfaceFontFamilySettingKey = "ui.font.family";
    public const string InterfaceFontSizeSettingKey = "ui.font.size";
    public const string InterfaceScalePercentSettingKey = "ui.scale.percent";
    public const string InterfaceThemeSettingKey = "ui.theme";
    public const string ApiTestModeSettingKey = "diagnostics.apiTestMode.enabled";

    private static readonly Lazy<IReadOnlyList<string>> SystemFontFamilies =
        new(LoadSystemFontFamilies);
    private static readonly IReadOnlyList<InterfaceScaleOption> InterfaceScaleOptions =
    [
        new(80, LanguageRuntime.GetString("Settings.Scale.Compact")),
        new(90, "90%"),
        new(100, LanguageRuntime.GetString("Settings.Scale.Default")),
        new(110, "110%"),
        new(125, "125%"),
        new(150, LanguageRuntime.GetString("Settings.Scale.Large"))
    ];
    private static readonly IReadOnlyList<InterfaceThemeOption> InterfaceThemeOptions =
    [
        new(InterfaceSettingsRuntime.LightThemeName, LanguageRuntime.GetString("Settings.Theme.Light")),
        new(InterfaceSettingsRuntime.DarkThemeName, LanguageRuntime.GetString("Settings.Theme.Dark")),
        new(InterfaceSettingsRuntime.CupertinoThemeName, LanguageRuntime.GetString("Settings.Theme.Cupertino")),
        new(InterfaceSettingsRuntime.MaterialThemeName, LanguageRuntime.GetString("Settings.Theme.Material"))
    ];

    private readonly IProviderProfileRepository _repository;
    private readonly IModelCatalogRepository _models;
    private readonly IModelAssignmentRepository _assignments;
    private readonly ISecretStore _secrets;
    private readonly IProviderGateway _gateway;
    private readonly IContextBudgetProvider _contextBudget;
    private readonly IUserInteractionService _interaction;
    private readonly IFileDialogService _fileDialog;
    private readonly IAppSettingsRepository? _appSettings;
    private readonly AppDataLocationService? _dataLocation;
    private readonly ITavernDeskDiagnostics _diagnostics;
    private readonly PlayerPersonaManagerViewModel? _personas;
    private readonly IInterfaceScaleRecommendationProvider?
        _interfaceScaleRecommendationProvider;
    private readonly HashSet<string> _persistedProfileIds = new(StringComparer.Ordinal);
    private readonly List<ProviderModel> _allCatalogModels = [];
    private readonly List<ProviderModel> _allAssignmentModels = [];
    private ProviderEditBuffer _editor = new();
    private ProviderProfile? _selectedProfile;
    private ProviderProfile? _catalogProvider;
    private ProviderProfile? _assignmentProvider;
    private ProviderModel? _selectedCatalogModel;
    private ProviderModel? _selectedAssignmentModel;
    private ModelFunctionOption _selectedFunction;
    private string _pendingApiKey = string.Empty;
    private string _keyStatus = LanguageRuntime.GetString("Settings.Key.NotSaved");
    private string _catalogSearchText = string.Empty;
    private string _assignmentSearchText = string.Empty;
    private string _modelContextLimit = "32768";
    private string _modelMaxOutputTokens = "4096";
    private string _assignmentContextLimit = "32768";
    private string _assignmentMaxOutputTokens = "4096";
    private string _assignmentTemperature = "0.8";
    private string _assignmentTopP = "1";
    private string _status = LanguageRuntime.GetString("Settings.Status.Intro");
    private bool _chatAutoScrollEnabled =
        InterfaceSettingsRuntime.DefaultChatAutoScroll;
    private string _interfaceFontFamily =
        InterfaceSettingsRuntime.DefaultFontFamily;
    private double _interfaceFontSize =
        InterfaceSettingsRuntime.DefaultFontSize;
    private InterfaceScaleOption _selectedInterfaceScaleOption =
        InterfaceScaleOptions.Single(option =>
            option.Percent == InterfaceSettingsRuntime.DefaultScalePercent);
    private InterfaceThemeOption _selectedInterfaceThemeOption =
        InterfaceThemeOptions.Single(option =>
            option.Value == InterfaceSettingsRuntime.DefaultThemeName);
    private SupportedLanguage _selectedLanguageOption =
        LanguageRuntime.Resolve(LanguageRuntime.CurrentCultureName);
    private string _interfaceScaleRecommendationText =
        LanguageRuntime.GetString("Settings.ScaleRecommendation.Pending");
    private string _interfaceSettingsStatus =
        LanguageRuntime.GetString("Settings.Interface.Intro");
    private string _dataRoot = string.Empty;
    private string _dataRootStatus = LanguageRuntime.GetString("Settings.DataRoot.Intro");
    private bool _isApiTestModeEnabled;
    private string _diagnosticsStatus =
        LanguageRuntime.GetString("Settings.Diagnostics.Status.Disabled");
    private string _apiTestOutputSummary =
        LanguageRuntime.Format("Settings.Diagnostics.OutputSummaryFormat", 0, "0 B");
    private SettingsPage _selectedSettingsPage = SettingsPage.Providers;
    private bool _isSelectedFunctionUnassigned;
    private int _catalogLoadVersion;
    private int _assignmentLoadVersion;

    public ProviderSettingsViewModel(
        IProviderProfileRepository repository,
        IModelCatalogRepository models,
        IModelAssignmentRepository assignments,
        ISecretStore secrets,
        IProviderGateway gateway,
        IContextBudgetProvider contextBudget,
        IUserInteractionService interaction,
        IGlobalPromptConfiguration globalPrompts,
        IFileDialogService fileDialog,
        IAppSettingsRepository? appSettings = null,
        AppDataLocationService? dataLocation = null,
        PlayerPersonaManagerViewModel? personas = null,
        IInterfaceScaleRecommendationProvider? interfaceScaleRecommendationProvider = null,
        ITavernDeskDiagnostics? diagnostics = null)
    {
        _repository = repository;
        _models = models;
        _assignments = assignments;
        _secrets = secrets;
        _gateway = gateway;
        _contextBudget = contextBudget;
        _interaction = interaction;
        _fileDialog = fileDialog;
        _appSettings = appSettings;
        _dataLocation = dataLocation;
        _diagnostics = diagnostics ?? NullTavernDeskDiagnostics.Instance;
        _personas = personas
                    ?? (appSettings is null
                        ? null
                        : new PlayerPersonaManagerViewModel(appSettings, interaction));
        _interfaceScaleRecommendationProvider = interfaceScaleRecommendationProvider;
        Prompts = new PromptSettingsViewModel(globalPrompts, fileDialog);
        FunctionOptions =
        [
            new(ModelFunctionKind.Chat, LanguageRuntime.GetString("Settings.Function.Chat")),
            new(ModelFunctionKind.MemoryUpdate, LanguageRuntime.GetString("Settings.Function.MemoryUpdate")),
            new(ModelFunctionKind.MemoryCompression, LanguageRuntime.GetString("Settings.Function.MemoryCompression")),
            new(ModelFunctionKind.GroupChat, LanguageRuntime.GetString("Settings.Function.GroupChat")),
            new(ModelFunctionKind.GroupMemoryMerge, LanguageRuntime.GetString("Settings.Function.GroupMemory")),
            new(ModelFunctionKind.Embedding, LanguageRuntime.GetString("Settings.Function.Embedding"))
        ];
        _selectedFunction = FunctionOptions[0];

        DeleteProviderCommand = new AsyncRelayCommand(
            DeleteProviderAsync,
            parameter => parameter is ProviderProfile);
        SaveCommand = new AsyncRelayCommand(
            SaveProviderAsync,
            () => SelectedProfile is not null);
        ClearKeyCommand = new AsyncRelayCommand(
            ClearKeyAsync,
            () => SelectedProfile is { SecretReference.Length: > 0 });
        RefreshModelsCommand = new AsyncRelayCommand(
            RefreshModelsAsync,
            () => CatalogProvider is not null);
        AddCustomModelCommand = new AsyncRelayCommand(
            AddCustomModelAsync,
            () => CatalogProvider is not null);
        SaveModelLimitsCommand = new AsyncRelayCommand(
            SaveModelLimitsAsync,
            () => SelectedCatalogModel is not null);
        SaveAssignmentCommand = new AsyncRelayCommand(
            SaveAssignmentAsync,
            () => AssignmentProvider is not null
                  && SelectedAssignmentModel is not null);
        ToggleReasoningCommand = new AsyncRelayCommand(
            ToggleReasoningAsync,
            parameter => parameter is ModelFunctionAssignmentOverview
            {
                IsReasoningAvailable: true
            });
        SaveInterfaceSettingsCommand = new AsyncRelayCommand(
            SaveInterfaceSettingsAsync,
            () => _appSettings is not null);
        RestoreInterfaceDefaultsCommand = new RelayCommand(
            RestoreInterfaceDefaults);
        PickDataRootCommand = new RelayCommand(PickDataRoot);
        ChangeDataRootCommand = new AsyncRelayCommand(
            ChangeDataRootAsync,
            () => _dataLocation is not null
                  && !_dataLocation.IsExternallyOverridden
                  && !string.IsNullOrWhiteSpace(DataRoot));
        SetApiTestModeCommand = new AsyncRelayCommand(
            parameter => SetApiTestModeAsync(parameter is true));
        OpenApiTestOutputCommand = new AsyncRelayCommand(
            OpenApiTestOutputAsync);
        ClearApiTestOutputCommand = new AsyncRelayCommand(
            ClearApiTestOutputAsync);
        _editor.PropertyChanged += OnEditorPropertyChanged;
    }

    public ObservableCollection<ProviderProfile> Profiles { get; } = [];
    public ObservableCollection<ProviderModel> VisibleCatalogModels { get; } = [];
    public ObservableCollection<ProviderModel> VisibleAssignmentModels { get; } = [];
    public ObservableCollection<ModelFunctionAssignmentOverview> AssignmentOverview { get; } = [];
    public PromptSettingsViewModel Prompts { get; }
    public PlayerPersonaManagerViewModel? Personas => _personas;
    public ProviderEditBuffer Editor
    {
        get => _editor;
        private set
        {
            if (ReferenceEquals(_editor, value))
            {
                return;
            }

            _editor.PropertyChanged -= OnEditorPropertyChanged;
            if (SetProperty(ref _editor, value))
            {
                _editor.PropertyChanged += OnEditorPropertyChanged;
                OnPropertyChanged(nameof(HasUnsavedChanges));
            }
        }
    }
    public IReadOnlyList<ModelFunctionOption> FunctionOptions { get; }
    public AsyncRelayCommand DeleteProviderCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand ClearKeyCommand { get; }
    public AsyncRelayCommand RefreshModelsCommand { get; }
    public AsyncRelayCommand AddCustomModelCommand { get; }
    public AsyncRelayCommand SaveModelLimitsCommand { get; }
    public AsyncRelayCommand SaveAssignmentCommand { get; }
    public AsyncRelayCommand ToggleReasoningCommand { get; }
    public AsyncRelayCommand SaveInterfaceSettingsCommand { get; }
    public RelayCommand RestoreInterfaceDefaultsCommand { get; }
    public RelayCommand PickDataRootCommand { get; }
    public AsyncRelayCommand ChangeDataRootCommand { get; }
    public AsyncRelayCommand SetApiTestModeCommand { get; }
    public AsyncRelayCommand OpenApiTestOutputCommand { get; }
    public AsyncRelayCommand ClearApiTestOutputCommand { get; }
    public IReadOnlyList<string> AvailableInterfaceFontFamilies =>
        SystemFontFamilies.Value;
    public IReadOnlyList<InterfaceScaleOption> AvailableInterfaceScaleOptions =>
        InterfaceScaleOptions;
    public IReadOnlyList<InterfaceThemeOption> AvailableInterfaceThemeOptions =>
        InterfaceThemeOptions;

    public ProviderProfile? SelectedProfile
    {
        get => _selectedProfile;
        private set
        {
            if (!SetProperty(ref _selectedProfile, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsGrokCliSelected));
            OnPropertyChanged(nameof(IsHttpApiSelected));
            OnPropertyChanged(nameof(ConnectionKindLabel));
            OnPropertyChanged(nameof(CredentialHelpText));
            SaveCommand.RaiseCanExecuteChanged();
            ClearKeyCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsGrokCliSelected =>
        SelectedProfile?.Id == ProviderProfileIds.GrokCli;

    public bool IsHttpApiSelected =>
        SelectedProfile is not null && !IsGrokCliSelected;

    public string ConnectionKindLabel => IsGrokCliSelected
        ? LanguageRuntime.GetString("Settings.Connection.Grok")
        : LanguageRuntime.GetString("Settings.Connection.OpenAi");

    public string CredentialHelpText => IsGrokCliSelected
        ? LanguageRuntime.GetString("Settings.Credential.Grok")
        : LanguageRuntime.GetString("Settings.Credential.OpenAi");

    public ProviderProfile? CatalogProvider
    {
        get => _catalogProvider;
        set
        {
            if (!SetProperty(ref _catalogProvider, value))
            {
                return;
            }

            var version = ++_catalogLoadVersion;
            _ = LoadCatalogSafeAsync(version);
            RefreshModelsCommand.RaiseCanExecuteChanged();
            AddCustomModelCommand.RaiseCanExecuteChanged();
        }
    }

    public ProviderProfile? AssignmentProvider
    {
        get => _assignmentProvider;
        set
        {
            if (!SetProperty(ref _assignmentProvider, value))
            {
                return;
            }

            var version = ++_assignmentLoadVersion;
            _ = LoadAssignmentModelsSafeAsync(version, preferredModelId: null);
            SaveAssignmentCommand.RaiseCanExecuteChanged();
        }
    }

    public ProviderModel? SelectedCatalogModel
    {
        get => _selectedCatalogModel;
        set
        {
            if (!SetProperty(ref _selectedCatalogModel, value))
            {
                return;
            }

            ModelContextLimit = (value?.ContextLimit ?? 32768).ToString();
            ModelMaxOutputTokens = (value?.MaxOutputTokens ?? 4096).ToString();
            SaveModelLimitsCommand.RaiseCanExecuteChanged();
        }
    }

    public ProviderModel? SelectedAssignmentModel
    {
        get => _selectedAssignmentModel;
        set
        {
            if (!SetProperty(ref _selectedAssignmentModel, value))
            {
                return;
            }

            if (value is not null && !IsEmbeddingFunctionSelected)
            {
                AssignmentContextLimit = value.ContextLimit.ToString();
                AssignmentMaxOutputTokens = value.MaxOutputTokens.ToString();
            }

            SaveAssignmentCommand.RaiseCanExecuteChanged();
        }
    }

    public ModelFunctionOption SelectedFunction
    {
        get => _selectedFunction;
        set
        {
            if (SetProperty(ref _selectedFunction, value))
            {
                OnPropertyChanged(nameof(IsEmbeddingFunctionSelected));
                IsSelectedFunctionUnassigned = false;
                var version = ++_assignmentLoadVersion;
                _ = LoadFunctionAssignmentSafeAsync(version);
            }
        }
    }

    public bool IsEmbeddingFunctionSelected =>
        SelectedFunction.Value == ModelFunctionKind.Embedding;

    public string CustomModelButtonText => LanguageRuntime.GetString("Settings.CustomModel.Add");

    public string PendingApiKey
    {
        get => _pendingApiKey;
        set
        {
            if (SetProperty(ref _pendingApiKey, value))
            {
                OnPropertyChanged(nameof(HasUnsavedChanges));
                KeyStatus = value.Length == 0
                    ? KeyStatusFor(SelectedProfile)
                    : LanguageRuntime.GetString("Settings.Key.Pending");
            }
        }
    }

    public string KeyStatus
    {
        get => _keyStatus;
        private set => SetProperty(ref _keyStatus, value);
    }

    public bool HasUnsavedChanges =>
        Editor.IsDirty || PendingApiKey.Length > 0;

    public string CatalogSearchText
    {
        get => _catalogSearchText;
        set
        {
            if (SetProperty(ref _catalogSearchText, value))
            {
                ApplyCatalogFilter();
            }
        }
    }

    public string AssignmentSearchText
    {
        get => _assignmentSearchText;
        set
        {
            if (SetProperty(ref _assignmentSearchText, value))
            {
                ApplyAssignmentFilter();
            }
        }
    }

    public string ModelContextLimit
    {
        get => _modelContextLimit;
        set => SetProperty(ref _modelContextLimit, value);
    }

    public string ModelMaxOutputTokens
    {
        get => _modelMaxOutputTokens;
        set => SetProperty(ref _modelMaxOutputTokens, value);
    }

    public string AssignmentContextLimit
    {
        get => _assignmentContextLimit;
        set => SetProperty(ref _assignmentContextLimit, value);
    }

    public string AssignmentMaxOutputTokens
    {
        get => _assignmentMaxOutputTokens;
        set => SetProperty(ref _assignmentMaxOutputTokens, value);
    }

    public string AssignmentTemperature
    {
        get => _assignmentTemperature;
        set => SetProperty(ref _assignmentTemperature, value);
    }

    public string AssignmentTopP
    {
        get => _assignmentTopP;
        set => SetProperty(ref _assignmentTopP, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public bool ChatAutoScrollEnabled
    {
        get => _chatAutoScrollEnabled;
        set => SetProperty(ref _chatAutoScrollEnabled, value);
    }

    public string InterfaceFontFamily
    {
        get => _interfaceFontFamily;
        set => SetProperty(ref _interfaceFontFamily, value);
    }

    public double InterfaceFontSize
    {
        get => _interfaceFontSize;
        set => SetProperty(ref _interfaceFontSize, value);
    }

    public InterfaceScaleOption SelectedInterfaceScaleOption
    {
        get => _selectedInterfaceScaleOption;
        set
        {
            if (value is null)
            {
                return;
            }

            var normalized = ResolveInterfaceScaleOption(value.Percent);
            if (!SetProperty(ref _selectedInterfaceScaleOption, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(InterfaceScalePercent));
            InterfaceSettingsRuntime.ApplyScale(normalized.Percent);
            InterfaceSettingsStatus = LanguageRuntime.Format(
                "Settings.ScalePreviewFormat",
                normalized.Percent);
        }
    }

    public int InterfaceScalePercent => SelectedInterfaceScaleOption.Percent;

    public InterfaceThemeOption SelectedInterfaceThemeOption
    {
        get => _selectedInterfaceThemeOption;
        set
        {
            if (value is null)
            {
                return;
            }

            var normalized = ResolveInterfaceThemeOption(value.Value);
            if (!SetProperty(ref _selectedInterfaceThemeOption, normalized))
            {
                return;
            }

            InterfaceSettingsRuntime.ApplyTheme(normalized.Value);
            InterfaceSettingsStatus = LanguageRuntime.Format(
                "Settings.ThemePreviewFormat",
                normalized.Label);
        }
    }

    public string InterfaceScaleRecommendationText
    {
        get => _interfaceScaleRecommendationText;
        private set => SetProperty(ref _interfaceScaleRecommendationText, value);
    }

    public string InterfaceSettingsStatus
    {
        get => _interfaceSettingsStatus;
        private set => SetProperty(ref _interfaceSettingsStatus, value);
    }

    public IReadOnlyList<SupportedLanguage> LanguageOptions =>
        LanguageRuntime.SupportedLanguages;

    public SupportedLanguage SelectedLanguageOption
    {
        get => _selectedLanguageOption;
        set
        {
            if (value is not null)
            {
                SetProperty(ref _selectedLanguageOption, LanguageRuntime.Resolve(value.CultureName));
            }
        }
    }

    public string DataRoot
    {
        get => _dataRoot;
        set
        {
            if (SetProperty(ref _dataRoot, value))
            {
                ChangeDataRootCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string DataRootConfigurationPath =>
        _dataLocation?.ConfigurationPath ?? string.Empty;

    public bool IsDataRootExternallyOverridden =>
        _dataLocation?.IsExternallyOverridden ?? true;

    public string DataRootStatus
    {
        get => _dataRootStatus;
        private set => SetProperty(ref _dataRootStatus, value);
    }

    public string ErrorLogDirectory => _diagnostics.ErrorLogDirectory;

    public string ApiTestOutputDirectory =>
        _diagnostics.ApiTestOutputDirectory;

    public bool IsApiTestModeEnabled
    {
        get => _isApiTestModeEnabled;
        private set => SetProperty(ref _isApiTestModeEnabled, value);
    }

    public string DiagnosticsStatus
    {
        get => _diagnosticsStatus;
        private set => SetProperty(ref _diagnosticsStatus, value);
    }

    public string ApiTestOutputSummary
    {
        get => _apiTestOutputSummary;
        private set => SetProperty(ref _apiTestOutputSummary, value);
    }

    public SettingsPage SelectedSettingsPage
    {
        get => _selectedSettingsPage;
        set => SetProperty(ref _selectedSettingsPage, value);
    }

    public bool IsSelectedFunctionUnassigned
    {
        get => _isSelectedFunctionUnassigned;
        private set => SetProperty(ref _isSelectedFunctionUnassigned, value);
    }

    public async Task LoadAsync()
    {
        LoadDataRootSettings();
        await LoadDiagnosticsSettingsAsync();
        await LoadInterfaceSettingsAsync();
        if (_personas is not null)
        {
            await _personas.LoadAsync();
        }
        var selectedProfileId = SelectedProfile?.Id;
        var catalogProviderId = CatalogProvider?.Id;
        var assignmentProviderId = AssignmentProvider?.Id;
        await ReloadProfilesAsync();
        SelectProfile(
            Profiles.FirstOrDefault(profile => profile.Id == selectedProfileId)
            ?? Profiles.FirstOrDefault());
        CatalogProvider = Profiles.FirstOrDefault(profile => profile.Id == catalogProviderId)
                          ?? Profiles.FirstOrDefault();
        _assignmentProvider = Profiles.FirstOrDefault(
                                  profile => profile.Id == assignmentProviderId)
                              ?? Profiles.FirstOrDefault();
        OnPropertyChanged(nameof(AssignmentProvider));
        await LoadFunctionAssignmentSafeAsync(++_assignmentLoadVersion);
        await RefreshAssignmentOverviewAsync();
        await Prompts.LoadAsync();
    }

    public void OpenPrompt(GlobalPromptKey key)
    {
        SelectedSettingsPage = SettingsPage.Prompts;
        Prompts.Open(key);
        Status = LanguageRuntime.GetString("Settings.PromptLocated");
    }

    public async Task<bool> ConfirmCanLeaveAsync()
    {
        if (!HasUnsavedChanges)
        {
            return true;
        }

        switch (_interaction.ConfirmUnsavedProviderChanges(
                    Editor.Name.Length == 0
                        ? LanguageRuntime.GetString("Settings.ProviderUnnamed")
                        : Editor.Name))
        {
            case UnsavedChangesDecision.Cancel:
                return false;
            case UnsavedChangesDecision.Save:
                await SaveProviderAsync();
                return !HasUnsavedChanges;
            case UnsavedChangesDecision.Discard:
                await DiscardCurrentEditsAsync();
                return true;
            default:
                return false;
        }
    }

    private async Task LoadInterfaceSettingsAsync()
    {
        if (_appSettings is null)
        {
            InterfaceSettingsRuntime.Apply(
                InterfaceFontFamily,
                InterfaceFontSize,
                ChatAutoScrollEnabled,
                InterfaceScalePercent,
                SelectedInterfaceThemeOption.Value);
            LoadInterfaceScaleRecommendation();
            return;
        }

        var autoScrollTask = _appSettings.GetAsync(ChatAutoScrollSettingKey);
        var fontFamilyTask = _appSettings.GetAsync(InterfaceFontFamilySettingKey);
        var fontSizeTask = _appSettings.GetAsync(InterfaceFontSizeSettingKey);
        var scaleTask = _appSettings.GetAsync(InterfaceScalePercentSettingKey);
        var themeTask = _appSettings.GetAsync(InterfaceThemeSettingKey);
        var languageTask = _appSettings.GetAsync(LanguageRuntime.SettingKey);
        await Task.WhenAll(
            autoScrollTask,
            fontFamilyTask,
            fontSizeTask,
            scaleTask,
            themeTask,
            languageTask);

        ChatAutoScrollEnabled =
            !bool.TryParse(autoScrollTask.Result, out var autoScroll)
            || autoScroll;
        InterfaceFontFamily = NormalizeFontFamily(fontFamilyTask.Result);
        InterfaceFontSize = NormalizeFontSize(fontSizeTask.Result);
        SelectedInterfaceScaleOption = await ResolveInitialScaleOptionAsync(scaleTask.Result);
        SelectedInterfaceThemeOption = ResolveInterfaceThemeOption(themeTask.Result);
        SelectedLanguageOption = LanguageRuntime.Resolve(languageTask.Result);
        InterfaceSettingsRuntime.Apply(
            InterfaceFontFamily,
            InterfaceFontSize,
            ChatAutoScrollEnabled,
            InterfaceScalePercent,
            SelectedInterfaceThemeOption.Value);
        LoadInterfaceScaleRecommendation();
        InterfaceSettingsStatus = LanguageRuntime.GetString("Settings.Interface.Loaded");
    }

    private void LoadDataRootSettings()
    {
        DataRoot = _dataLocation?.CurrentRoot ?? string.Empty;
        DataRootStatus = _dataLocation is null
            ? LanguageRuntime.GetString("Settings.DataRoot.Unavailable")
            : _dataLocation.IsExternallyOverridden
                ? LanguageRuntime.GetString("Settings.DataRoot.Overridden")
                : LanguageRuntime.Format(
                    "Settings.DataRoot.ConfigFormat",
                    _dataLocation.ConfigurationPath);
    }

    private async Task LoadDiagnosticsSettingsAsync()
    {
        try
        {
            var shouldEnable = false;
            if (_appSettings is not null)
            {
                shouldEnable = bool.TryParse(
                    await _appSettings.GetAsync(ApiTestModeSettingKey),
                    out var saved)
                    && saved;
            }

            await _diagnostics.SetApiTestModeEnabledAsync(shouldEnable);
            IsApiTestModeEnabled = _diagnostics.IsApiTestModeEnabled;
            DiagnosticsStatus = IsApiTestModeEnabled
                ? LanguageRuntime.GetString("Settings.Diagnostics.Status.Enabled")
                : LanguageRuntime.GetString("Settings.Diagnostics.Status.Disabled");
        }
        catch (Exception exception)
        {
            IsApiTestModeEnabled = false;
            DiagnosticsStatus = LanguageRuntime.Format(
                "Settings.Diagnostics.Status.EnableFailedFormat",
                LanguageRuntime.ErrorMessage(exception));
        }

        await RefreshApiTestOutputSummaryAsync();
    }

    private async Task SetApiTestModeAsync(bool enabled)
    {
        if (enabled == IsApiTestModeEnabled)
        {
            return;
        }

        try
        {
            if (enabled)
            {
                await _diagnostics.SetApiTestModeEnabledAsync(true);
                if (_appSettings is not null)
                {
                    try
                    {
                        await _appSettings.SetAsync(
                            ApiTestModeSettingKey,
                            bool.TrueString);
                    }
                    catch
                    {
                        await _diagnostics.SetApiTestModeEnabledAsync(false);
                        throw;
                    }
                }
            }
            else
            {
                if (_appSettings is not null)
                {
                    await _appSettings.SetAsync(
                        ApiTestModeSettingKey,
                        bool.FalseString);
                }

                await _diagnostics.SetApiTestModeEnabledAsync(false);
            }

            IsApiTestModeEnabled = _diagnostics.IsApiTestModeEnabled;
            DiagnosticsStatus = IsApiTestModeEnabled
                ? LanguageRuntime.GetString("Settings.Diagnostics.Status.Enabled")
                : LanguageRuntime.GetString("Settings.Diagnostics.Status.Disabled");
        }
        catch (Exception exception)
        {
            IsApiTestModeEnabled = _diagnostics.IsApiTestModeEnabled;
            OnPropertyChanged(nameof(IsApiTestModeEnabled));
            DiagnosticsStatus = LanguageRuntime.Format(
                enabled
                    ? "Settings.Diagnostics.Status.EnableFailedFormat"
                    : "Settings.Diagnostics.Status.DisableFailedFormat",
                LanguageRuntime.ErrorMessage(exception));
        }

        await RefreshApiTestOutputSummaryAsync();
    }

    private async Task OpenApiTestOutputAsync()
    {
        try
        {
            Directory.CreateDirectory(ApiTestOutputDirectory);
            _fileDialog.OpenFolder(ApiTestOutputDirectory);
            DiagnosticsStatus = LanguageRuntime.GetString(
                "Settings.Diagnostics.Status.FolderOpened");
        }
        catch (Exception exception)
        {
            DiagnosticsStatus = LanguageRuntime.Format(
                "Settings.Diagnostics.Status.OpenFailedFormat",
                LanguageRuntime.ErrorMessage(exception));
        }

        await RefreshApiTestOutputSummaryAsync();
    }

    private async Task ClearApiTestOutputAsync()
    {
        if (!_interaction.ConfirmClearApiTestOutput(ApiTestOutputDirectory))
        {
            return;
        }

        try
        {
            var deletedEntries = await _diagnostics.ClearApiTestOutputAsync();
            DiagnosticsStatus = LanguageRuntime.Format(
                "Settings.Diagnostics.Status.ClearedFormat",
                deletedEntries);
        }
        catch (ApiTestOutputBusyException)
        {
            DiagnosticsStatus = LanguageRuntime.GetString(
                "Settings.Diagnostics.Status.ClearBusy");
        }
        catch (Exception exception)
        {
            DiagnosticsStatus = LanguageRuntime.Format(
                "Settings.Diagnostics.Status.ClearFailedFormat",
                LanguageRuntime.ErrorMessage(exception));
        }

        await RefreshApiTestOutputSummaryAsync();
    }

    private async Task RefreshApiTestOutputSummaryAsync()
    {
        try
        {
            var summary = await _diagnostics.GetApiTestOutputSummaryAsync();
            ApiTestOutputSummary = LanguageRuntime.Format(
                "Settings.Diagnostics.OutputSummaryFormat",
                summary.FileCount,
                FormatFileSize(summary.TotalBytes));
        }
        catch (Exception exception)
        {
            ApiTestOutputSummary = LanguageRuntime.Format(
                "Settings.Diagnostics.OutputSummaryFailedFormat",
                LanguageRuntime.ErrorMessage(exception));
        }
    }

    private static string FormatFileSize(long bytes)
    {
        var units = new[] { "B", "KiB", "MiB", "GiB" };
        var value = Math.Max(0, bytes);
        var unitIndex = 0;
        var displayValue = (double)value;
        while (displayValue >= 1024 && unitIndex < units.Length - 1)
        {
            displayValue /= 1024;
            unitIndex++;
        }

        return string.Format(
            CultureInfo.CurrentUICulture,
            unitIndex == 0 ? "{0:0} {1}" : "{0:0.##} {1}",
            displayValue,
            units[unitIndex]);
    }

    private void PickDataRoot()
    {
        var selected = _fileDialog.PickDataRoot();
        if (!string.IsNullOrWhiteSpace(selected))
        {
            DataRoot = Path.GetFullPath(selected);
            DataRootStatus = LanguageRuntime.GetString("Settings.DataRoot.Selected");
        }
    }

    private async Task ChangeDataRootAsync()
    {
        if (_dataLocation is null)
        {
            DataRootStatus = LanguageRuntime.GetString("Settings.DataRoot.Unavailable");
            return;
        }

        if (_dataLocation.IsExternallyOverridden)
        {
            DataRootStatus = LanguageRuntime.GetString("Settings.DataRoot.OverrideBlocked");
            return;
        }

        string requestedRoot;
        try
        {
            requestedRoot = Path.GetFullPath(DataRoot.Trim());
        }
        catch (Exception exception) when (exception is ArgumentException or IOException)
        {
            DataRootStatus = LanguageRuntime.Format("Settings.DataRoot.InvalidFormat", LanguageRuntime.ErrorMessage(exception));
            return;
        }

        if (string.Equals(
                requestedRoot,
                _dataLocation.CurrentRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            DataRoot = requestedRoot;
            DataRootStatus = LanguageRuntime.GetString("Settings.DataRoot.Unchanged");
            return;
        }

        var decision = _interaction.ConfirmDataRootMigration(
            _dataLocation.CurrentRoot,
            requestedRoot);
        if (decision == DataRootMigrationDecision.Cancel)
        {
            DataRootStatus = LanguageRuntime.GetString("Settings.DataRoot.Cancelled");
            return;
        }

        try
        {
            var mode = decision == DataRootMigrationDecision.CopyCurrentData
                ? DataRootMigrationMode.CopyCurrentData
                : DataRootMigrationMode.KeepTargetAsIs;
            var result = await _dataLocation.ChangeRootAsync(
                requestedRoot,
                mode);
            DataRoot = result.NewRoot;
            DataRootStatus = result.Migrated
                ? LanguageRuntime.Format(
                    "Settings.DataRoot.MigratedFormat",
                    result.CopiedFiles,
                    result.CopiedBytes)
                : LanguageRuntime.GetString("Settings.DataRoot.Switched");
        }
        catch (Exception exception)
        {
            DataRootStatus = LanguageRuntime.Format("Settings.DataRoot.FailedFormat", LanguageRuntime.ErrorMessage(exception));
        }
    }

    private async Task SaveInterfaceSettingsAsync()
    {
        if (_appSettings is null)
        {
            InterfaceSettingsStatus = LanguageRuntime.GetString("Settings.Interface.RepositoryUnavailable");
            return;
        }

        InterfaceFontFamily = NormalizeFontFamily(InterfaceFontFamily);
        InterfaceFontSize = NormalizeFontSize(InterfaceFontSize);
        await Task.WhenAll(
            _appSettings.SetAsync(
                ChatAutoScrollSettingKey,
                ChatAutoScrollEnabled.ToString(CultureInfo.InvariantCulture)),
            _appSettings.SetAsync(
                InterfaceFontFamilySettingKey,
                InterfaceFontFamily),
            _appSettings.SetAsync(
                InterfaceFontSizeSettingKey,
                InterfaceFontSize.ToString(CultureInfo.InvariantCulture)),
            _appSettings.SetAsync(
                InterfaceScalePercentSettingKey,
                InterfaceScalePercent.ToString(CultureInfo.InvariantCulture)),
            _appSettings.SetAsync(
                InterfaceThemeSettingKey,
                SelectedInterfaceThemeOption.Value),
            _appSettings.SetAsync(
                LanguageRuntime.SettingKey,
                SelectedLanguageOption.CultureName));
        InterfaceSettingsRuntime.Apply(
            InterfaceFontFamily,
            InterfaceFontSize,
            ChatAutoScrollEnabled,
            InterfaceScalePercent,
            SelectedInterfaceThemeOption.Value);
        InterfaceSettingsStatus = LanguageRuntime.Format(
            "Settings.Interface.SavedFormat",
            SelectedLanguageOption.NativeName,
            InterfaceScalePercent,
            SelectedInterfaceThemeOption.Label,
            InterfaceFontFamily,
            InterfaceFontSize,
            ChatAutoScrollEnabled
                ? LanguageRuntime.GetString("Settings.Interface.AutoScrollOn")
                : LanguageRuntime.GetString("Settings.Interface.AutoScrollOff"));
    }

    private void RestoreInterfaceDefaults()
    {
        ChatAutoScrollEnabled = InterfaceSettingsRuntime.DefaultChatAutoScroll;
        InterfaceFontFamily = InterfaceSettingsRuntime.DefaultFontFamily;
        InterfaceFontSize = InterfaceSettingsRuntime.DefaultFontSize;
        SelectedInterfaceScaleOption = ResolveInterfaceScaleOption(
            InterfaceSettingsRuntime.DefaultScalePercent);
        SelectedInterfaceThemeOption = ResolveInterfaceThemeOption(
            InterfaceSettingsRuntime.DefaultThemeName);
        SelectedLanguageOption = LanguageRuntime.Resolve(LanguageRuntime.DefaultCultureName);
        InterfaceSettingsStatus = LanguageRuntime.GetString("Settings.Interface.Restored");
    }

    private string NormalizeFontFamily(string? value)
    {
        var requested = string.IsNullOrWhiteSpace(value)
            ? InterfaceSettingsRuntime.DefaultFontFamily
            : value.Trim();
        return AvailableInterfaceFontFamilies.FirstOrDefault(font =>
                   string.Equals(font, requested, StringComparison.OrdinalIgnoreCase))
               ?? InterfaceSettingsRuntime.DefaultFontFamily;
    }

    private static double NormalizeFontSize(string? value) =>
        double.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? NormalizeFontSize(parsed)
            : InterfaceSettingsRuntime.DefaultFontSize;

    private static double NormalizeFontSize(double value) =>
        double.IsFinite(value)
            ? Math.Clamp(
                Math.Round(value, MidpointRounding.AwayFromZero),
                InterfaceSettingsRuntime.MinimumFontSize,
                InterfaceSettingsRuntime.MaximumFontSize)
            : InterfaceSettingsRuntime.DefaultFontSize;

    private async Task<InterfaceScaleOption> ResolveInitialScaleOptionAsync(string? savedValue)
    {
        if (!string.IsNullOrWhiteSpace(savedValue))
        {
            return ResolveInterfaceScaleOption(savedValue);
        }

        var recommendation = _interfaceScaleRecommendationProvider?.GetRecommendation();
        var option = ResolveInterfaceScaleOption(
            recommendation?.Percent
            ?? InterfaceSettingsRuntime.DefaultScalePercent);
        if (_appSettings is not null)
        {
            await _appSettings.SetAsync(
                InterfaceScalePercentSettingKey,
                option.Percent.ToString(CultureInfo.InvariantCulture));
        }

        return option;
    }

    private static InterfaceScaleOption ResolveInterfaceScaleOption(string? value) =>
        int.TryParse(
            value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? ResolveInterfaceScaleOption(parsed)
            : ResolveInterfaceScaleOption(
                InterfaceSettingsRuntime.DefaultScalePercent);

    private static InterfaceScaleOption ResolveInterfaceScaleOption(int value)
    {
        var normalized = InterfaceSettingsRuntime.NormalizeScalePercent(value);
        return InterfaceScaleOptions
            .OrderBy(option => Math.Abs(option.Percent - normalized))
            .ThenBy(option => Math.Abs(
                option.Percent - InterfaceSettingsRuntime.DefaultScalePercent))
            .First();
    }

    private static InterfaceThemeOption ResolveInterfaceThemeOption(string? value)
    {
        var normalized = InterfaceSettingsRuntime.NormalizeThemeName(value);
        return InterfaceThemeOptions.Single(option => option.Value == normalized);
    }

    private void LoadInterfaceScaleRecommendation()
    {
        if (_interfaceScaleRecommendationProvider is null)
        {
            return;
        }

        try
        {
            var recommendation = _interfaceScaleRecommendationProvider
                .GetRecommendation();
            if (recommendation is null)
            {
                return;
            }

            var option = ResolveInterfaceScaleOption(recommendation.Percent);
            var reason = string.IsNullOrWhiteSpace(recommendation.Reason)
                ? LanguageRuntime.GetString("Settings.ScaleRecommendation.DefaultReason")
                : recommendation.Reason.Trim();
            InterfaceScaleRecommendationText = LanguageRuntime.Format(
                "Settings.ScaleRecommendation.Format",
                option.Percent,
                reason);
        }
        catch
        {
            InterfaceScaleRecommendationText =
                LanguageRuntime.GetString("Settings.ScaleRecommendation.Failed");
        }
    }

    private static IReadOnlyList<string> LoadSystemFontFamilies()
    {
        var fonts = Fonts.SystemFontFamilies
            .Select(font => font.Source)
            .Where(font => !string.IsNullOrWhiteSpace(font))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(font => font, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (!fonts.Contains(
                InterfaceSettingsRuntime.DefaultFontFamily,
                StringComparer.OrdinalIgnoreCase))
        {
            fonts.Insert(0, InterfaceSettingsRuntime.DefaultFontFamily);
        }

        return fonts;
    }

    private async Task ReloadProfilesAsync()
    {
        Profiles.Clear();
        _persistedProfileIds.Clear();
        foreach (var profile in (await _repository.ListAsync())
                     .Where(profile => ProviderProfileIds.IsSupportedAdapter(
                         profile.AdapterKind)))
        {
            Profiles.Add(profile);
            _persistedProfileIds.Add(profile.Id);
        }
    }

    public async Task<ProviderProfile?> AddCustomProviderAsync(
        string name,
        string baseUrl)
    {
        var normalizedName = name.Trim();
        var normalizedBaseUrl = baseUrl.Trim().TrimEnd('/');
        if (normalizedName.Length == 0)
        {
            Status = LanguageRuntime.GetString("Validation.Provider.NameRequired");
            return null;
        }

        if (!Uri.TryCreate(normalizedBaseUrl, UriKind.Absolute, out var baseUri)
            || baseUri.Scheme is not ("http" or "https"))
        {
            Status = LanguageRuntime.GetString("Validation.Provider.InvalidUrl");
            return null;
        }

        if (!string.IsNullOrEmpty(baseUri.Query)
            || !string.IsNullOrEmpty(baseUri.Fragment))
        {
            Status = LanguageRuntime.GetString("Validation.Provider.UrlQueryNotAllowed");
            return null;
        }

        if (baseUri.AbsolutePath.EndsWith(
                "/chat",
                StringComparison.OrdinalIgnoreCase)
            || baseUri.AbsolutePath.EndsWith(
                "/chat/completions",
                StringComparison.OrdinalIgnoreCase))
        {
            Status = LanguageRuntime.GetString("Validation.Provider.UrlPathHint");
            return null;
        }

        var profile = new ProviderProfile
        {
            Id = $"custom-{Guid.NewGuid():N}",
            Name = normalizedName,
            AdapterKind = ProviderAdapterKind.OpenAiCompatible,
            BaseUrl = normalizedBaseUrl,
            RequestTimeoutSeconds = 300,
            IsEnabled = true
        };
        try
        {
            await _repository.UpsertAsync(profile);
            await RefreshProfileReferencesAsync(profile.Id);
        }
        catch (Exception exception)
        {
            Status = LanguageRuntime.Format("Settings.Provider.AddFailedFormat", LanguageRuntime.ErrorMessage(exception));
            return null;
        }

        Status = LanguageRuntime.Format("Settings.Provider.AddedFormat", profile.Name);
        return Profiles.FirstOrDefault(item => item.Id == profile.Id) ?? profile;
    }

    private async Task DeleteProviderAsync(object? parameter)
    {
        if (parameter is not ProviderProfile requested)
        {
            return;
        }

        var profile = Profiles.FirstOrDefault(item => item.Id == requested.Id);
        if (profile is null)
        {
            return;
        }

        if (SelectedProfile?.Id == profile.Id
            && HasUnsavedChanges
            && !await ConfirmCanLeaveAsync())
        {
            return;
        }

        profile = Profiles.FirstOrDefault(item => item.Id == requested.Id);
        if (profile is null
            || !_interaction.ConfirmProviderDeletion(profile.Name))
        {
            return;
        }

        var secretReference = profile.SecretReference;
        try
        {
            if (_persistedProfileIds.Contains(profile.Id))
            {
                await _repository.DeleteAsync(profile.Id);
            }
        }
        catch (Exception exception)
        {
            Status = LanguageRuntime.Format("Settings.Provider.DeleteFailedFormat", LanguageRuntime.ErrorMessage(exception));
            return;
        }

        string? secretCleanupWarning = null;
        if (secretReference.Length > 0)
        {
            try
            {
                await _secrets.DeleteAsync(secretReference);
            }
            catch (Exception exception)
            {
                // The database deletion already removed this provider and its
                // dependent rows. An encrypted orphan is safer than restoring
                // a partial provider without its models and assignments.
                secretCleanupWarning =
                    LanguageRuntime.Format(
                        "Settings.Provider.KeyCleanupFailedFormat",
                        LanguageRuntime.ErrorMessage(exception));
            }
        }

        Profiles.Remove(profile);
        _persistedProfileIds.Remove(profile.Id);
        if (SelectedProfile?.Id == profile.Id)
        {
            SelectProfile(Profiles.FirstOrDefault());
        }

        if (CatalogProvider?.Id == profile.Id)
        {
            CatalogProvider = Profiles.FirstOrDefault();
        }

        if (AssignmentProvider?.Id == profile.Id)
        {
            _assignmentProvider = Profiles.FirstOrDefault();
            OnPropertyChanged(nameof(AssignmentProvider));
        }

        await LoadFunctionAssignmentSafeAsync(++_assignmentLoadVersion);
        await RefreshAssignmentOverviewAsync();
        Status = secretCleanupWarning
                 ?? LanguageRuntime.Format("Settings.Provider.DeletedFormat", profile.Name);
    }

    public async Task<bool> SelectProfileAsync(ProviderProfile profile)
    {
        if (profile.Id == SelectedProfile?.Id)
        {
            return true;
        }

        if (!await ConfirmCanLeaveAsync())
        {
            return false;
        }

        SelectProfile(profile);
        return true;
    }

    private void SelectProfile(ProviderProfile? profile)
    {
        SelectedProfile = profile;
        PendingApiKey = string.Empty;
        if (profile is null)
        {
            KeyStatus = LanguageRuntime.GetString("Settings.Provider.NoneSelected");
            return;
        }

        var editor = new ProviderEditBuffer();
        editor.Load(profile);
        Editor = editor;
        KeyStatus = KeyStatusFor(profile);
    }

    private void OnEditorPropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ProviderEditBuffer.IsDirty))
        {
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
    }

    private async Task SaveProviderAsync()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        if (!Editor.TryApplyTo(SelectedProfile, out var error))
        {
            Status = error;
            return;
        }

        if (SelectedProfile.AdapterKind == ProviderAdapterKind.GrokCli
            && !string.IsNullOrWhiteSpace(PendingApiKey))
        {
            Status = LanguageRuntime.GetString("Settings.Provider.GrokNoKey");
            return;
        }

        var previousReference = SelectedProfile.SecretReference;
        string? newlySavedReference = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(PendingApiKey))
            {
                newlySavedReference = await _secrets.SaveAsync(
                    SelectedProfile.Id,
                    PendingApiKey.Trim());
                SelectedProfile.SecretReference = newlySavedReference;
            }

            await _repository.UpsertAsync(SelectedProfile);
        }
        catch
        {
            SelectedProfile.SecretReference = previousReference;
            if (newlySavedReference is not null)
            {
                try
                {
                    await _secrets.DeleteAsync(newlySavedReference);
                }
                catch
                {
                    // The database still points to the previous reference.
                    // A protected, unreferenced file is safer than losing that state.
                }
            }

            throw;
        }

        var cleanupWarning = string.Empty;
        if (newlySavedReference is not null
            && previousReference.Length > 0
            && !string.Equals(
                previousReference,
                newlySavedReference,
                StringComparison.Ordinal))
        {
            try
            {
                await _secrets.DeleteAsync(previousReference);
            }
            catch (Exception exception)
            {
                cleanupWarning =
                    LanguageRuntime.Format(
                        "Settings.Provider.OldKeyCleanupWarningFormat",
                        LanguageRuntime.ErrorMessage(exception));
            }
        }

        _persistedProfileIds.Add(SelectedProfile.Id);
        Editor.MarkSaved();
        PendingApiKey = string.Empty;
        KeyStatus = KeyStatusFor(SelectedProfile);
        Status = LanguageRuntime.Format(
            "Settings.Provider.SavedFormat",
            SelectedProfile.Name,
            cleanupWarning);
        await RefreshProfileReferencesAsync(SelectedProfile.Id);
    }

    private async Task ClearKeyAsync()
    {
        if (SelectedProfile is null
            || string.IsNullOrWhiteSpace(SelectedProfile.SecretReference)
            || !_interaction.ConfirmSecretClear(SelectedProfile.Name))
        {
            return;
        }

        var previousReference = SelectedProfile.SecretReference;
        SelectedProfile.SecretReference = string.Empty;
        SelectedProfile.UpdatedAt = DateTimeOffset.Now;
        try
        {
            await _repository.UpsertAsync(SelectedProfile);
        }
        catch
        {
            SelectedProfile.SecretReference = previousReference;
            throw;
        }

        var cleanupWarning = string.Empty;
        try
        {
            await _secrets.DeleteAsync(previousReference);
        }
        catch (Exception exception)
        {
            cleanupWarning =
                LanguageRuntime.Format(
                    "Settings.Provider.KeyDisabledCleanupWarningFormat",
                    LanguageRuntime.ErrorMessage(exception));
        }

        PendingApiKey = string.Empty;
        KeyStatus = LanguageRuntime.GetString("Settings.Provider.KeyCleared");
        ClearKeyCommand.RaiseCanExecuteChanged();
        Status = LanguageRuntime.Format(
            "Settings.Provider.KeyClearedFormat",
            SelectedProfile.Name,
            cleanupWarning);
    }

    private async Task DiscardCurrentEditsAsync()
    {
        PendingApiKey = string.Empty;
        if (SelectedProfile is null)
        {
            return;
        }

        if (!_persistedProfileIds.Contains(SelectedProfile.Id))
        {
            Profiles.Remove(SelectedProfile);
            SelectProfile(Profiles.FirstOrDefault());
        }
        else
        {
            var stored = await _repository.GetAsync(SelectedProfile.Id);
            if (stored is not null)
            {
                var index = Profiles.IndexOf(SelectedProfile);
                if (index >= 0)
                {
                    Profiles[index] = stored;
                }

                SelectProfile(stored);
            }
        }
    }

    private async Task RefreshProfileReferencesAsync(string selectedId)
    {
        var catalogId = CatalogProvider?.Id;
        var assignmentId = AssignmentProvider?.Id;
        await ReloadProfilesAsync();
        SelectProfile(Profiles.FirstOrDefault(profile => profile.Id == selectedId));
        CatalogProvider = Profiles.FirstOrDefault(profile => profile.Id == catalogId)
                          ?? Profiles.FirstOrDefault();
        _assignmentProvider = Profiles.FirstOrDefault(profile => profile.Id == assignmentId)
                              ?? Profiles.FirstOrDefault();
        OnPropertyChanged(nameof(AssignmentProvider));
        await RefreshAssignmentOverviewAsync();
    }

    private async Task RefreshModelsAsync()
    {
        if (CatalogProvider is null)
        {
            return;
        }

        if (!_persistedProfileIds.Contains(CatalogProvider.Id))
        {
            Status = LanguageRuntime.GetString("Settings.Models.SaveProviderFirst");
            return;
        }

        try
        {
            Status = LanguageRuntime.Format("Settings.Models.RequestingFormat", CatalogProvider.Name);
            var descriptors = (await _gateway.RefreshModelsAsync(
                    CatalogProvider.Id))
                .ToList();
            string? embeddingStatus = null;
            if (_gateway is IEmbeddingModelCatalogGateway embeddingGateway)
            {
                try
                {
                    var dedicatedEmbeddingDescriptors =
                        await embeddingGateway.RefreshEmbeddingModelsAsync(
                            CatalogProvider.Id);
                    descriptors.AddRange(dedicatedEmbeddingDescriptors);
                }
                catch (HttpRequestException exception)
                {
                    embeddingStatus =
                        LanguageRuntime.Format(
                            "Settings.Models.EmbeddingRefreshFailedFormat",
                            LanguageRuntime.ErrorMessage(exception));
                }
            }

            descriptors = descriptors
                .Where(model => !string.IsNullOrWhiteSpace(model.ModelId))
                .GroupBy(model => model.ModelId.Trim(), StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
            await _models.ReplaceAsync(CatalogProvider.Id, descriptors);

            await LoadCatalogSafeAsync(++_catalogLoadVersion);
            Status = embeddingStatus is null
                ? LanguageRuntime.Format(
                    "Settings.Models.RefreshedFormat",
                    CatalogProvider.Name,
                    descriptors.Count)
                : LanguageRuntime.Format(
                    "Settings.Models.RefreshedWithNoticeFormat",
                    CatalogProvider.Name,
                    descriptors.Count,
                    embeddingStatus);
        }
        catch (Exception exception)
        {
            Status = LanguageRuntime.Format("Settings.Models.RefreshFailedFormat", LanguageRuntime.ErrorMessage(exception));
        }
    }

    private async Task AddCustomModelAsync()
    {
        if (CatalogProvider is null)
        {
            Status = LanguageRuntime.GetString("Settings.Models.SelectProvider");
            return;
        }

        if (!_persistedProfileIds.Contains(CatalogProvider.Id))
        {
            Status = LanguageRuntime.GetString("Settings.Models.SaveBeforeCustom");
            return;
        }

        var modelId = await _interaction.PromptModelNameAsync(CatalogSearchText);
        if (modelId is null)
        {
            return;
        }

        await SaveCustomModelCoreAsync(modelId);
    }

    public async Task<bool> SaveCustomModelAsync(string modelId)
    {
        return await SaveCustomModelCoreAsync(modelId);
    }

    private async Task<bool> SaveCustomModelCoreAsync(string modelId)
    {
        if (CatalogProvider is null)
        {
            Status = LanguageRuntime.GetString("Settings.Models.SelectProvider");
            return false;
        }

        if (!_persistedProfileIds.Contains(CatalogProvider.Id))
        {
            Status = LanguageRuntime.GetString("Settings.Models.SaveBeforeCustom");
            return false;
        }

        var normalizedModelId = modelId.Trim();
        if (normalizedModelId.Length == 0)
        {
            Status = LanguageRuntime.GetString("Settings.Models.IdRequired");
            return false;
        }

        if (normalizedModelId.Contains('\r')
            || normalizedModelId.Contains('\n'))
        {
            Status = LanguageRuntime.GetString("Settings.Models.IdSingleLine");
            return false;
        }

        try
        {
            await _models.UpsertAsync(new ProviderModel
            {
                ProviderId = CatalogProvider.Id,
                ModelId = normalizedModelId,
                DisplayName = normalizedModelId,
                ContextLimit = 32768,
                MaxOutputTokens = 4096,
                SupportsStreaming = true,
                ModelKind = ModelCatalogKind.Custom,
                UpdatedAt = DateTimeOffset.Now
            });

            CatalogSearchText = normalizedModelId;
            await LoadCatalogSafeAsync(++_catalogLoadVersion);
            SelectedCatalogModel = VisibleCatalogModels.FirstOrDefault(model =>
                model.ModelId == normalizedModelId
                && model.ModelKind == ModelCatalogKind.Custom);
            Status = LanguageRuntime.Format("Settings.Models.CustomSavedFormat", normalizedModelId);
            return true;
        }
        catch (Exception exception)
        {
            Status = LanguageRuntime.Format("Settings.Models.CustomSaveFailedFormat", LanguageRuntime.ErrorMessage(exception));
            return false;
        }
    }

    private async Task LoadCatalogSafeAsync(int version)
    {
        try
        {
            var provider = CatalogProvider;
            var models = provider is null
                ? Array.Empty<ProviderModel>()
                : await _models.ListAsync(provider.Id);
            if (version != _catalogLoadVersion)
            {
                return;
            }

            _allCatalogModels.Clear();
            _allCatalogModels.AddRange(models);
            ApplyCatalogFilter();
        }
        catch (Exception exception) when (version == _catalogLoadVersion)
        {
            Status = LanguageRuntime.Format("Settings.Models.ReadFailedFormat", LanguageRuntime.ErrorMessage(exception));
        }
    }

    private void ApplyCatalogFilter()
    {
        var selectedId = SelectedCatalogModel?.ModelId;
        var query = CatalogSearchText.Trim();
        VisibleCatalogModels.Clear();
        foreach (var model in _allCatalogModels.Where(model =>
                     query.Length == 0
                     || model.ModelId.Contains(query, StringComparison.OrdinalIgnoreCase)
                     || model.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            VisibleCatalogModels.Add(model);
        }

        SelectedCatalogModel = VisibleCatalogModels.FirstOrDefault(model =>
                                   model.ModelId == selectedId)
                               ?? VisibleCatalogModels.FirstOrDefault();
    }

    private async Task SaveModelLimitsAsync()
    {
        if (SelectedCatalogModel is null)
        {
            Status = LanguageRuntime.GetString("Settings.Models.SelectModelStatus");
            return;
        }

        if (!TryReadLimits(
                ModelContextLimit,
                ModelMaxOutputTokens,
                out var contextLimit,
                out var maxOutput,
                out var error))
        {
            Status = error;
            return;
        }

        SelectedCatalogModel.ContextLimit = contextLimit;
        SelectedCatalogModel.MaxOutputTokens = maxOutput;
        SelectedCatalogModel.UpdatedAt = DateTimeOffset.Now;
        await _models.UpsertAsync(SelectedCatalogModel);
        Status = LanguageRuntime.Format(
            "Settings.Models.LimitsSavedFormat",
            SelectedCatalogModel.ModelId);
    }

    private async Task LoadFunctionAssignmentSafeAsync(int version)
    {
        try
        {
            var assignment = await _assignments.GetAsync(SelectedFunction.Value);
            if (version != _assignmentLoadVersion)
            {
                return;
            }

            IsSelectedFunctionUnassigned = assignment is null;

            var provider = Profiles.FirstOrDefault(profile =>
                               profile.Id == assignment?.ProviderId)
                           ?? AssignmentProvider
                           ?? Profiles.FirstOrDefault();
            _assignmentProvider = provider;
            OnPropertyChanged(nameof(AssignmentProvider));
            if (assignment is null)
            {
                AssignmentContextLimit = "32768";
                AssignmentMaxOutputTokens = "4096";
                AssignmentTemperature = "0.8";
                AssignmentTopP = "1";
            }

            await LoadAssignmentModelsSafeAsync(
                version,
                assignment?.ModelId);
            if (version != _assignmentLoadVersion || assignment is null)
            {
                return;
            }

            AssignmentContextLimit = assignment.ContextLimit.ToString();
            AssignmentMaxOutputTokens = assignment.MaxOutputTokens.ToString();
            AssignmentTemperature = assignment.Temperature.ToString("0.###");
            AssignmentTopP = assignment.TopP.ToString("0.###");
        }
        catch (Exception exception) when (version == _assignmentLoadVersion)
        {
            IsSelectedFunctionUnassigned = false;
            Status = LanguageRuntime.Format("Settings.Assignments.ReadFailedFormat", LanguageRuntime.ErrorMessage(exception));
        }
    }

    private async Task RefreshAssignmentOverviewAsync()
    {
        var assignments = (await _assignments.ListAsync())
            .ToDictionary(assignment => assignment.FunctionKind);
        AssignmentOverview.Clear();
        foreach (var option in FunctionOptions)
        {
            assignments.TryGetValue(option.Value, out var assignment);
            var provider = assignment is null
                ? null
                : Profiles.FirstOrDefault(profile =>
                    profile.Id == assignment.ProviderId);
            var reasoningAvailable =
                assignment is not null
                && option.Value != ModelFunctionKind.Embedding
                && ModelFeatureSupport.SupportsOpenRouterDeepSeekReasoning(
                    provider,
                    assignment.ModelId);
            AssignmentOverview.Add(new ModelFunctionAssignmentOverview(
                option.Value,
                option.Label,
                assignment is null
                    ? LanguageRuntime.GetString("Settings.Assignments.Unassigned")
                    : provider?.Name
                      ?? LanguageRuntime.GetString("Settings.Assignments.ProviderDeleted"),
                assignment?.ModelId ?? "—",
                reasoningAvailable,
                reasoningAvailable && assignment!.ReasoningEnabled));
        }
    }

    private async Task LoadAssignmentModelsSafeAsync(
        int version,
        string? preferredModelId)
    {
        try
        {
            var provider = AssignmentProvider;
            var models = provider is null
                ? Array.Empty<ProviderModel>()
                : (await _models.ListAsync(provider.Id)).ToArray();
            if (version != _assignmentLoadVersion)
            {
                return;
            }

            _allAssignmentModels.Clear();
            _allAssignmentModels.AddRange(models);
            ApplyAssignmentFilter(preferredModelId);
        }
        catch (Exception exception) when (version == _assignmentLoadVersion)
        {
            Status = LanguageRuntime.Format(
                "Settings.Assignments.ModelsReadFailedFormat",
                LanguageRuntime.ErrorMessage(exception));
        }
    }

    private void ApplyAssignmentFilter(string? preferredModelId = null)
    {
        var selectedId = preferredModelId ?? SelectedAssignmentModel?.ModelId;
        var query = AssignmentSearchText.Trim();
        VisibleAssignmentModels.Clear();
        foreach (var model in _allAssignmentModels.Where(model =>
                     query.Length == 0
                     || model.ModelId.Contains(query, StringComparison.OrdinalIgnoreCase)
                     || model.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            VisibleAssignmentModels.Add(model);
        }

        SelectedAssignmentModel = VisibleAssignmentModels.FirstOrDefault(model =>
                                      model.ModelId == selectedId)
                                  ?? VisibleAssignmentModels.FirstOrDefault();
    }

    private async Task SaveAssignmentAsync()
    {
        if (AssignmentProvider is null || SelectedAssignmentModel is null)
        {
            Status = LanguageRuntime.GetString("Settings.Assignments.SelectProviderModel");
            return;
        }

        int contextLimit;
        int maxOutput;
        double temperature;
        double topP;
        if (IsEmbeddingFunctionSelected)
        {
            // The existing assignment table requires generation columns.
            // Embedding does not use them; neutral persisted values preserve
            // the shared provider/model assignment path without exposing
            // meaningless generation controls in the UI.
            contextLimit = 1024;
            maxOutput = 1;
            temperature = 0;
            topP = 1;
        }
        else
        {
            if (!TryReadLimits(
                    AssignmentContextLimit,
                    AssignmentMaxOutputTokens,
                    out contextLimit,
                    out maxOutput,
                    out var error))
            {
                Status = error;
                return;
            }

            if (!double.TryParse(AssignmentTemperature, out temperature)
                || temperature is < 0 or > 2)
            {
                Status = LanguageRuntime.GetString("Settings.Assignments.TemperatureRange");
                return;
            }

            if (!double.TryParse(AssignmentTopP, out topP)
                || topP is <= 0 or > 1)
            {
                Status = LanguageRuntime.GetString("Settings.Assignments.TopPRange");
                return;
            }
        }

        var previous = await _assignments.GetAsync(SelectedFunction.Value);
        var reasoningAvailable =
            !IsEmbeddingFunctionSelected
            &&
            ModelFeatureSupport.SupportsOpenRouterDeepSeekReasoning(
                AssignmentProvider,
                SelectedAssignmentModel.ModelId);
        var assignment = new ModelFunctionAssignment
        {
            FunctionKind = SelectedFunction.Value,
            ProviderId = AssignmentProvider.Id,
            ModelId = SelectedAssignmentModel.ModelId,
            ContextLimit = contextLimit,
            MaxOutputTokens = maxOutput,
            Temperature = temperature,
            TopP = topP,
            ReasoningEnabled =
                reasoningAvailable
                && previous?.ProviderId == AssignmentProvider.Id
                && previous.ModelId == SelectedAssignmentModel.ModelId
                && previous.ReasoningEnabled,
            UpdatedAt = DateTimeOffset.Now
        };
        await _assignments.UpsertAsync(assignment);
        IsSelectedFunctionUnassigned = false;
        await RefreshAssignmentOverviewAsync();
        Status = LanguageRuntime.Format(
            "Settings.Assignments.SavedFormat",
            SelectedFunction.Label,
            AssignmentProvider.Name,
            assignment.ModelId);
    }

    private async Task ToggleReasoningAsync(object? parameter)
    {
        if (parameter is not ModelFunctionAssignmentOverview overview)
        {
            return;
        }

        var assignment = await _assignments.GetAsync(overview.Value);
        var provider = assignment is null
            ? null
            : Profiles.FirstOrDefault(item => item.Id == assignment.ProviderId);
        if (assignment is null
            || !ModelFeatureSupport.SupportsOpenRouterDeepSeekReasoning(
                provider,
                assignment.ModelId))
        {
            Status = LanguageRuntime.GetString("Settings.Assignments.NotOpenRouterDeepSeek");
            await RefreshAssignmentOverviewAsync();
            return;
        }

        assignment.ReasoningEnabled = !assignment.ReasoningEnabled;
        assignment.UpdatedAt = DateTimeOffset.Now;
        await _assignments.UpsertAsync(assignment);
        await RefreshAssignmentOverviewAsync();
        Status = LanguageRuntime.Format(
            "Settings.Assignments.ReasoningFormat",
            overview.Label,
            assignment.ReasoningEnabled
                ? LanguageRuntime.GetString("Settings.Assignments.ReasoningOn")
                : LanguageRuntime.GetString("Settings.Assignments.ReasoningOff"));
    }

    private static bool TryReadLimits(
        string contextText,
        string outputText,
        out int contextLimit,
        out int maxOutput,
        out string error)
    {
        error = string.Empty;
        if (!int.TryParse(contextText, out contextLimit)
            || contextLimit is < 1024 or > 4_194_304)
        {
            maxOutput = 0;
            error = LanguageRuntime.GetString("Settings.Limits.ContextRange");
            return false;
        }

        if (!int.TryParse(outputText, out maxOutput)
            || maxOutput < 1
            || maxOutput > contextLimit)
        {
            error = LanguageRuntime.GetString("Settings.Limits.OutputRange");
            return false;
        }

        return true;
    }

    private static string KeyStatusFor(ProviderProfile? profile) =>
        profile switch
        {
            { Id: ProviderProfileIds.GrokCli, SecretReference.Length: > 0 } =>
                LanguageRuntime.GetString("Settings.Key.GrokLegacy"),
            { Id: ProviderProfileIds.GrokCli } =>
                LanguageRuntime.GetString("Settings.Key.GrokNone"),
            { SecretReference.Length: > 0 } =>
                LanguageRuntime.GetString("Settings.Key.Protected"),
            _ => LanguageRuntime.GetString("Settings.Key.Optional")
        };
}

public enum SettingsPage
{
    Providers,
    ModelCatalog,
    Assignments,
    Prompts,
    Personas,
    DefaultBehavior,
    Interface,
    Data
}

public sealed record ModelFunctionOption(
    ModelFunctionKind Value,
    string Label);

public sealed record ModelFunctionAssignmentOverview(
    ModelFunctionKind Value,
    string Label,
    string ProviderName,
    string ModelId,
    bool IsReasoningAvailable = false,
    bool IsReasoningEnabled = false)
{
    public string TargetText => ModelId == "—"
        ? ProviderName
        : $"{ProviderName} / {ModelId}";

    public string ReasoningStateText =>
        IsReasoningEnabled ? "ON" : "OFF";
}
