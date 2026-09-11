using System.Globalization;
using System.Windows.Media;
using TavernDesk.App.Localization;
using TavernDesk.App.Presentation;
using TavernDesk.App.Services;
using TavernDesk.Core.Abstractions;

namespace TavernDesk.App.ViewModels;

public sealed class InterfaceSettingsViewModel : ViewModelBase
{
    public const string ChatAutoScrollSettingKey = "ui.chat.autoScroll";
    public const string InterfaceFontFamilySettingKey = "ui.font.family";
    public const string InterfaceFontSizeSettingKey = "ui.font.size";
    public const string InterfaceScalePercentSettingKey = "ui.scale.percent";
    public const string InterfaceThemeSettingKey = "ui.theme";

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

    private readonly IAppSettingsRepository? _appSettings;
    private readonly IInterfaceScaleRecommendationProvider? _interfaceScaleRecommendationProvider;
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

    public InterfaceSettingsViewModel(
        IAppSettingsRepository? appSettings = null,
        IInterfaceScaleRecommendationProvider? interfaceScaleRecommendationProvider = null)
    {
        _appSettings = appSettings;
        _interfaceScaleRecommendationProvider = interfaceScaleRecommendationProvider;
        SaveInterfaceSettingsCommand = new AsyncRelayCommand(
            SaveInterfaceSettingsAsync,
            () => _appSettings is not null);
        RestoreInterfaceDefaultsCommand = new RelayCommand(RestoreInterfaceDefaults);
    }

    public AsyncRelayCommand SaveInterfaceSettingsCommand { get; }
    public RelayCommand RestoreInterfaceDefaultsCommand { get; }

    public IReadOnlyList<string> AvailableInterfaceFontFamilies =>
        SystemFontFamilies.Value;
    public IReadOnlyList<InterfaceScaleOption> AvailableInterfaceScaleOptions =>
        InterfaceScaleOptions;
    public IReadOnlyList<InterfaceThemeOption> AvailableInterfaceThemeOptions =>
        InterfaceThemeOptions;

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

    public async Task LoadAsync()
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
}
