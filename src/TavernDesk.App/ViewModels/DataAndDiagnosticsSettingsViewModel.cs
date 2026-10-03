using System.Globalization;
using System.IO;
using TavernDesk.App.Localization;
using TavernDesk.App.Presentation;
using TavernDesk.App.Services;
using TavernDesk.Core.Abstractions;
using TavernDesk.Infrastructure.Diagnostics;
using TavernDesk.Infrastructure.Storage;

namespace TavernDesk.App.ViewModels;

public sealed class DataAndDiagnosticsSettingsViewModel : ViewModelBase
{
    public const string ApiTestModeSettingKey = "diagnostics.apiTestMode.enabled";
    private readonly IAppSettingsRepository? _appSettings;
    private readonly AppDataLocationService? _dataLocation;
    private readonly ITavernDeskDiagnostics _diagnostics;
    private readonly IFileDialogService _fileDialog;
    private readonly IUserInteractionService _interaction;
    private string _dataRoot = string.Empty;
    private string _dataRootStatus = LanguageRuntime.GetString("Settings.DataRoot.Intro");
    private bool _isApiTestModeEnabled;
    private string _diagnosticsStatus =
        LanguageRuntime.GetString("Settings.Diagnostics.Status.Disabled");
    private string _apiTestOutputSummary =
        LanguageRuntime.Format("Settings.Diagnostics.OutputSummaryFormat", 0, "0 B");

    public DataAndDiagnosticsSettingsViewModel(
        IAppSettingsRepository? appSettings,
        AppDataLocationService? dataLocation,
        ITavernDeskDiagnostics diagnostics,
        IFileDialogService fileDialog,
        IUserInteractionService interaction)
    {
        _appSettings = appSettings;
        _dataLocation = dataLocation;
        _diagnostics = diagnostics;
        _fileDialog = fileDialog;
        _interaction = interaction;
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
    }

    public RelayCommand PickDataRootCommand { get; }
    public AsyncRelayCommand ChangeDataRootCommand { get; }
    public AsyncRelayCommand SetApiTestModeCommand { get; }
    public AsyncRelayCommand OpenApiTestOutputCommand { get; }
    public AsyncRelayCommand ClearApiTestOutputCommand { get; }
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

    public async Task LoadAsync()
    {
        LoadDataRootSettings();
        await LoadDiagnosticsSettingsAsync();
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
        if (_dataLocation is not null && !_dataLocation.IsExternallyOverridden)
        {
            try
            {
                if (_dataLocation.PendingRoot is { } pendingRoot)
                {
                    DataRoot = pendingRoot;
                    DataRootStatus = LanguageRuntime.Format("Settings.DataRoot.PendingFormat",
                        _dataLocation.CurrentRoot, pendingRoot);
                }
            }
            catch (Exception exception)
            {
                DataRootStatus = LanguageRuntime.Format("Settings.DataRoot.FailedFormat", LanguageRuntime.ErrorMessage(exception));
            }
        }
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
            await _dataLocation.ScheduleRootChangeAsync(requestedRoot, DataRootMigrationMode.KeepTargetAsIs);
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
            await _dataLocation.ScheduleRootChangeAsync(
                requestedRoot,
                mode);
            DataRoot = requestedRoot;
            DataRootStatus = LanguageRuntime.GetString("Settings.DataRoot.Scheduled");
        }
        catch (Exception exception)
        {
            DataRootStatus = LanguageRuntime.Format("Settings.DataRoot.FailedFormat", LanguageRuntime.ErrorMessage(exception));
        }
    }

}
