using System.Windows;
using TavernDesk.App.Localization;
using TavernDesk.App.Presentation;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;

namespace TavernDesk.App.ViewModels;

public sealed class CampaignSettingsPanelViewModel : ViewModelBase, IDisposable
{
    private readonly ICampaignRepository _campaigns;
    private readonly ICampaignMemoryRepository? _campaignMemories;
    private readonly ICampaignMemoryUpdateService? _campaignMemoryUpdater;
    private readonly Func<CampaignAggregate?> _currentGame;
    private readonly Func<bool> _isGame;
    private readonly Func<bool> _isBusy;
    private readonly Func<Func<Task>, Task> _runUiAsync;
    private readonly Func<string, Task> _reloadGame;
    private readonly Action<string> _setStatus;
    private readonly Action _onStateChanged;
    private bool _disposed;
    private bool _isMemoryUpdating;
    private readonly HashSet<string> _activeMemoryOperations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _memoryTokensByOperation = new(StringComparer.Ordinal);
    private string _memoryProgressText = string.Empty;
    private int _memoryReceivedTokens;
    private bool _campaignMemoryPending;
    private bool _campaignMemoryNeedsEstablish;
    private string _campaignMemoryStatusText = LanguageRuntime.GetString("Campaigns.Memory.Unchecked");
    private string? _campaignMemoryLastError;
    private string _campaignContextTokenBudgetText = "15000";
    private string _campaignPlayerHistoryBudgetText = "12000";
    private string _campaignGmHistoryBudgetText = "20000";
    private string _campaignMemoryUpdateIntervalRoundsText = "3";
    private string _campaignMemoryPendingTokenThresholdText = "4000";
    private string _campaignMemorySettingsStatusText = string.Empty;

    public CampaignSettingsPanelViewModel(
        ICampaignRepository campaigns,
        ICampaignMemoryRepository? memories,
        ICampaignMemoryUpdateService? updater,
        Func<CampaignAggregate?> currentGame,
        Func<bool> isGame,
        Func<bool> isBusy,
        Func<Func<Task>, Task> runUiAsync,
        Func<string, Task> reloadGame,
        Action<string> setStatus,
        Action onStateChanged)
    {
        _campaigns = campaigns;
        _campaignMemories = memories;
        _campaignMemoryUpdater = updater;
        _currentGame = currentGame;
        _isGame = isGame;
        _isBusy = isBusy;
        _runUiAsync = runUiAsync;
        _reloadGame = reloadGame;
        _setStatus = setStatus;
        _onStateChanged = onStateChanged;
        RetryCampaignMemoryCommand = new AsyncRelayCommand(RetryCampaignMemoryAsync);
        ToggleCampaignMemoryCommand = new AsyncRelayCommand(ToggleCampaignMemoryAsync, () => CanToggleCampaignMemory);
        SaveCampaignMemorySettingsCommand = new AsyncRelayCommand(SaveCampaignMemorySettingsAsync);
        if (updater is not null) updater.ProgressChanged += OnCampaignMemoryProgressChanged;
    }

    public AsyncRelayCommand RetryCampaignMemoryCommand { get; }
    public AsyncRelayCommand ToggleCampaignMemoryCommand { get; }
    public AsyncRelayCommand SaveCampaignMemorySettingsCommand { get; }

    public void SwitchGame(string? previousCampaignId, string campaignId)
    {
        if (string.Equals(previousCampaignId, campaignId, StringComparison.Ordinal)) return;
        _activeMemoryOperations.Clear();
        _memoryTokensByOperation.Clear();
        _memoryReceivedTokens = 0;
        _isMemoryUpdating = false;
    }

    public void NotifyGameStateChanged()
    {
        OnPropertyChanged(nameof(IsMemoryUpdating));
        OnPropertyChanged(nameof(MemoryProgressText));
        OnPropertyChanged(nameof(MemoryReceivedTokenText));
        OnPropertyChanged(nameof(CampaignMemoryStatusText));
        OnPropertyChanged(nameof(IsCampaignMemoryEnabled));
        OnPropertyChanged(nameof(CampaignMemoryToggleText));
        OnPropertyChanged(nameof(CanToggleCampaignMemory));
        OnPropertyChanged(nameof(CampaignMemoryActionText));
        OnPropertyChanged(nameof(ShowCampaignMemoryAction));
        OnPropertyChanged(nameof(CanRetryCampaignMemory));
        ToggleCampaignMemoryCommand.RaiseCanExecuteChanged();
    }

    private bool IsCurrent(CampaignAggregate game) => !_disposed && ReferenceEquals(_currentGame(), game);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_campaignMemoryUpdater is not null)
            _campaignMemoryUpdater.ProgressChanged -= OnCampaignMemoryProgressChanged;
    }

    public bool IsMemoryUpdating => _isMemoryUpdating;
    public string MemoryProgressText => _memoryProgressText;
    public string MemoryReceivedTokenText => _isMemoryUpdating
        ? LanguageRuntime.Format("Campaigns.Request.TokensFormat", _memoryReceivedTokens)
        : string.Empty;

    public string CampaignMemoryStatusText => _campaignMemoryStatusText;
    public bool IsCampaignMemoryEnabled => _currentGame()?.Campaign.MemoryEnabled == true;
    public string CampaignMemoryToggleText =>
        IsCampaignMemoryEnabled
            ? LanguageRuntime.GetString("Campaigns.Memory.ToggleOn")
            : LanguageRuntime.GetString("Campaigns.Memory.ToggleOff");
    public bool CanToggleCampaignMemory =>
        _isGame() && !_isBusy() && _currentGame() is not null;
    public string CampaignMemoryActionText => _campaignMemoryNeedsEstablish
        ? LanguageRuntime.GetString("Campaigns.Memory.Establish")
        : LanguageRuntime.GetString("Campaigns.Memory.Retry");
    public bool ShowCampaignMemoryAction =>
        _isGame()
        && IsCampaignMemoryEnabled
        && _campaignMemoryUpdater is not null
        && (_campaignMemoryNeedsEstablish
            || (!string.IsNullOrWhiteSpace(_campaignMemoryLastError)
                && _campaignMemoryPending));
    public bool CanRetryCampaignMemory =>
        ShowCampaignMemoryAction
        && !_isBusy()
        && !IsMemoryUpdating;

    public string CampaignContextTokenBudgetText
    {
        get => _campaignContextTokenBudgetText;
        set => SetProperty(ref _campaignContextTokenBudgetText, value);
    }

    public string CampaignPlayerHistoryBudgetText
    {
        get => _campaignPlayerHistoryBudgetText;
        set => SetProperty(ref _campaignPlayerHistoryBudgetText, value);
    }

    public string CampaignGmHistoryBudgetText
    {
        get => _campaignGmHistoryBudgetText;
        set => SetProperty(ref _campaignGmHistoryBudgetText, value);
    }

    public string CampaignMemoryUpdateIntervalRoundsText
    {
        get => _campaignMemoryUpdateIntervalRoundsText;
        set => SetProperty(ref _campaignMemoryUpdateIntervalRoundsText, value);
    }

    public string CampaignMemoryPendingTokenThresholdText
    {
        get => _campaignMemoryPendingTokenThresholdText;
        set => SetProperty(
            ref _campaignMemoryPendingTokenThresholdText,
            value);
    }

    public string CampaignMemorySettingsStatusText
    {
        get => _campaignMemorySettingsStatusText;
        private set => SetProperty(
            ref _campaignMemorySettingsStatusText,
            value);
    }

    public void Prepare()
    {
        var game = _currentGame();
        if (game is null || _disposed)
        {
            return;
        }

        CampaignContextTokenBudgetText =
            game.Campaign.ContextTokenBudget.ToString();
        CampaignPlayerHistoryBudgetText =
            game.Campaign.PlayerHistoryBudget.ToString();
        CampaignGmHistoryBudgetText =
            game.Campaign.GmHistoryBudget.ToString();
        CampaignMemoryUpdateIntervalRoundsText =
            game.Campaign.MemoryUpdateIntervalRounds.ToString();
        CampaignMemoryPendingTokenThresholdText =
            game.Campaign.MemoryUpdatePendingTokenThreshold.ToString();
        CampaignMemorySettingsStatusText = string.Empty;
    }

    private async Task SaveCampaignMemorySettingsAsync()
    {
        var game = _currentGame();
        if (game is null || _disposed)
        {
            return;
        }

        if (!TryParseSetting(
                CampaignContextTokenBudgetText,
                8_000,
                200_000,
                LanguageRuntime.GetString("Campaigns.MemorySetting.InputBudget"),
                out var contextTokenBudget)
            || !TryParseSetting(
                CampaignPlayerHistoryBudgetText,
                512,
                200_000,
                LanguageRuntime.GetString("Campaigns.MemorySetting.PlayerHistory"),
                out var playerHistoryBudget)
            || !TryParseSetting(
                CampaignGmHistoryBudgetText,
                512,
                200_000,
                LanguageRuntime.GetString("Campaigns.MemorySetting.GmHistory"),
                out var gmHistoryBudget)
            || !TryParseSetting(
                CampaignMemoryUpdateIntervalRoundsText,
                1,
                50,
                LanguageRuntime.GetString("Campaigns.MemorySetting.UpdateInterval"),
                out var memoryUpdateIntervalRounds)
            || !TryParseSetting(
                CampaignMemoryPendingTokenThresholdText,
                1_000,
                50_000,
                LanguageRuntime.GetString("Campaigns.MemorySetting.PendingThreshold"),
                out var memoryUpdatePendingTokenThreshold))
        {
            return;
        }

        await _runUiAsync(async () =>
        {
            try
            {
                var campaignId = game.Campaign.Id;
                await _campaigns.UpdateContextSettingsAsync(
                    campaignId,
                    game.Campaign.StateVersion,
                    new CampaignContextSettingsUpdate(
                        playerHistoryBudget,
                        gmHistoryBudget,
                        contextTokenBudget,
                        memoryUpdateIntervalRounds,
                        memoryUpdatePendingTokenThreshold));
                await _reloadGame(campaignId);
                if (_disposed || _currentGame()?.Campaign.Id != campaignId) return;
                Prepare();
                CampaignMemorySettingsStatusText =
                    LanguageRuntime.GetString("Campaigns.MemorySetting.Saved");
                _setStatus(LanguageRuntime.GetString("Campaigns.MemorySetting.StatusSaved"));
            }
            catch (Exception exception)
            {
                if (IsCurrent(game)) CampaignMemorySettingsStatusText = LanguageRuntime.ErrorMessage(exception);
            }
        });
    }

    private bool TryParseSetting(
        string value,
        int minimum,
        int maximum,
        string label,
        out int result)
    {
        if (!int.TryParse(value, out result)
            || result < minimum
            || result > maximum)
        {
            CampaignMemorySettingsStatusText =
                LanguageRuntime.Format(
                    "Campaigns.MemorySetting.RangeFormat",
                    label,
                    minimum,
                    maximum);
            return false;
        }

        return true;
    }

    private async Task ToggleCampaignMemoryAsync()
    {
        var game = _currentGame();
        if (game is null || _disposed)
        {
            return;
        }

        var campaignId = game.Campaign.Id;
        var enabled = !game.Campaign.MemoryEnabled;
        var expectedStateVersion = game.Campaign.StateVersion;
        await _runUiAsync(async () =>
        {
            await _campaigns.UpdateMemoryEnabledAsync(
                campaignId,
                expectedStateVersion,
                enabled);
            await _reloadGame(campaignId);
            if (_disposed || _currentGame()?.Campaign.Id != campaignId) return;
            _setStatus(enabled
                ? LanguageRuntime.GetString("Campaigns.Memory.Enabled")
                : LanguageRuntime.GetString("Campaigns.Memory.Disabled"));
        });
    }

    private async Task RetryCampaignMemoryAsync(object? _)
    {
        var game = _currentGame();
        if (_disposed || game is null
            || !game.Campaign.MemoryEnabled
            || _campaignMemoryUpdater is null)
        {
            return;
        }

        var campaignId = game.Campaign.Id;
        var latestResolution = LatestCompletedGmResolution(game);
        if (latestResolution is null)
        {
            _campaignMemoryPending = false;
            SetCampaignMemoryStatus(
                LanguageRuntime.GetString("Campaigns.Memory.NothingToEstablish"));
            return;
        }

        await _runUiAsync(async () =>
        {
            _campaignMemoryLastError = null;
            SetCampaignMemoryStatus(
                LanguageRuntime.GetString("Campaigns.Memory.Updating"));
            var result = await _campaignMemoryUpdater.UpdateAsync(
                campaignId,
                latestResolution.SequenceNo,
                force: true,
                CancellationToken.None);
            if (!IsCurrent(game)) return;
            if (!result.Succeeded)
            {
                _campaignMemoryLastError = result.ErrorMessage
                                            ?? result.Status.ToString();
            }

            await RefreshStatusAsync();
            if (!IsCurrent(game)) return;
            _setStatus(result.Succeeded
                ? LanguageRuntime.GetString("Campaigns.Memory.Updated")
                : LanguageRuntime.Format(
                    "Campaigns.Memory.UpdateIncompleteFormat",
                    _campaignMemoryLastError));
        });
    }

    public async Task RefreshStatusAsync()
    {
        if (_disposed) return;
        var game = _currentGame();
        if (game is null || !game.Campaign.MemoryEnabled)
        {
            _campaignMemoryPending = false;
            _campaignMemoryNeedsEstablish = false;
            _campaignMemoryLastError = null;
            SetCampaignMemoryStatus(
                LanguageRuntime.GetString("Campaigns.Memory.UpgradeDisabled"));
            return;
        }

        if (_campaignMemories is null)
        {
            _campaignMemoryPending = false;
            _campaignMemoryNeedsEstablish = false;
            SetCampaignMemoryStatus(
                LanguageRuntime.GetString("Campaigns.Memory.NotEnabled"));
            return;
        }

        var latestResolution = LatestCompletedGmResolution(game);
        var latestResolutionSequence = latestResolution?.SequenceNo ?? 0;
        var gmCheckpointTask = _campaignMemories.GetCheckpointAsync(
            game.Campaign.Id,
            CampaignMemoryScope.GameMaster);
        var publicCheckpointTask = _campaignMemories.GetCheckpointAsync(
            game.Campaign.Id,
            CampaignMemoryScope.Public);
        await Task.WhenAll(gmCheckpointTask, publicCheckpointTask);
        if (!IsCurrent(game)) return;
        var gmSequence = gmCheckpointTask.Result?.LastEventSequence ?? 0;
        var publicSequence = publicCheckpointTask.Result?.LastEventSequence ?? 0;
        _campaignMemoryPending = latestResolutionSequence > gmSequence
                                 || latestResolutionSequence > publicSequence;
        _campaignMemoryNeedsEstablish = latestResolution is not null
                                         && gmCheckpointTask.Result is null
                                         && publicCheckpointTask.Result is null;
        OnPropertyChanged(nameof(CanRetryCampaignMemory));
        OnPropertyChanged(nameof(CampaignMemoryActionText));
        OnPropertyChanged(nameof(ShowCampaignMemoryAction));
        if (!string.IsNullOrWhiteSpace(_campaignMemoryLastError)
            && _campaignMemoryPending)
        {
            SetCampaignMemoryStatus(
                LanguageRuntime.Format(
                    "Campaigns.Memory.UpdateFailedFormat",
                    latestResolutionSequence));
        }
        else if (latestResolution is null)
        {
            SetCampaignMemoryStatus(
                LanguageRuntime.GetString("Campaigns.Memory.NoResolution"));
        }
        else if (gmCheckpointTask.Result is null
                 && publicCheckpointTask.Result is null)
        {
            SetCampaignMemoryStatus(
                LanguageRuntime.Format(
                    "Campaigns.Memory.NotEstablishedFormat",
                    latestResolutionSequence));
        }
        else if (_campaignMemoryPending)
        {
            SetCampaignMemoryStatus(
                LanguageRuntime.Format(
                    "Campaigns.Memory.PendingFormat",
                    gmSequence,
                    publicSequence,
                    latestResolutionSequence));
        }
        else
        {
            _campaignMemoryLastError = null;
            SetCampaignMemoryStatus(
                LanguageRuntime.Format(
                    "Campaigns.Memory.UpdatedThroughFormat",
                    latestResolutionSequence));
        }
    }

    private static CampaignEvent? LatestCompletedGmResolution(
        CampaignAggregate aggregate)
    {
        return aggregate.Events
            .Where(item =>
                item.Kind == CampaignEventKind.GmResolution
                && item.IsLocked
                && item.GenerationStatus == CampaignGenerationStatus.Completed)
            .OrderBy(item => item.SequenceNo)
            .LastOrDefault();
    }

    private void SetCampaignMemoryStatus(string value)
    {
        if (SetProperty(ref _campaignMemoryStatusText, value))
        {
            OnPropertyChanged(nameof(CanRetryCampaignMemory));
            OnPropertyChanged(nameof(CampaignMemoryActionText));
            OnPropertyChanged(nameof(ShowCampaignMemoryAction));
        }
    }

    private void OnCampaignMemoryProgressChanged(
        object? sender,
        CampaignMemoryUpdateProgress progress)
    {
        if (_disposed || progress.CampaignId != _currentGame()?.Campaign.Id)
        {
            return;
        }

        RunOnUi(() =>
        {
            if (_disposed || progress.CampaignId != _currentGame()?.Campaign.Id) return;
            var operationId = progress.OperationId
                              ?? $"{progress.CampaignId}|memory";
            switch (progress.Status)
            {
                case CampaignMemoryUpdateProgressStatus.Started:
                    _activeMemoryOperations.Add(operationId);
                    _memoryTokensByOperation[operationId] = 0;
                    _isMemoryUpdating = true;
                    _memoryReceivedTokens = _memoryTokensByOperation.Values.Sum();
                    _memoryProgressText = LanguageRuntime.GetString(
                        "Campaigns.Memory.ProgressDefault");
                    _setStatus(_memoryProgressText);
                    break;
                case CampaignMemoryUpdateProgressStatus.Receiving:
                    _activeMemoryOperations.Add(operationId);
                    _memoryTokensByOperation[operationId] = Math.Max(
                        _memoryTokensByOperation.GetValueOrDefault(operationId),
                        progress.ReceivedTokens);
                    _isMemoryUpdating = true;
                    _memoryReceivedTokens = _memoryTokensByOperation.Values.Sum();
                    _memoryProgressText = progress.Scope is null
                        ? LanguageRuntime.GetString("Campaigns.Memory.Progress")
                        : LanguageRuntime.Format(
                            "Campaigns.Memory.ProgressScopeFormat",
                            MemoryScopeName(progress.Scope.Value));
                    break;
                case CampaignMemoryUpdateProgressStatus.Completed:
                    CompleteMemoryOperation(
                        operationId,
                        LanguageRuntime.GetString("Campaigns.Memory.ProgressDone"));
                    break;
                case CampaignMemoryUpdateProgressStatus.Failed:
                    CompleteMemoryOperation(
                        operationId,
                        string.IsNullOrWhiteSpace(progress.Message)
                            ? LanguageRuntime.GetString("Campaigns.Memory.ProgressFailed")
                            : LanguageRuntime.Format(
                                "Campaigns.Memory.ProgressFailedFormat",
                                LanguageRuntime.BackendMessage(
                                    progress.Message,
                                    "Common.NoFurtherDetails")));
                    break;
            }

            OnPropertyChanged(nameof(IsMemoryUpdating));
            OnPropertyChanged(nameof(MemoryProgressText));
            OnPropertyChanged(nameof(MemoryReceivedTokenText));
            _onStateChanged();
        });
    }

    private void CompleteMemoryOperation(
        string operationId,
        string terminalMessage)
    {
        _activeMemoryOperations.Remove(operationId);
        _memoryTokensByOperation.Remove(operationId);
        _isMemoryUpdating = _activeMemoryOperations.Count > 0;
        _memoryReceivedTokens = _memoryTokensByOperation.Values.Sum();
        if (!_isMemoryUpdating)
        {
            _memoryProgressText = terminalMessage;
            _setStatus(_memoryProgressText);
        }
    }

    private static string MemoryScopeName(CampaignMemoryScope scope) =>
        scope == CampaignMemoryScope.GameMaster
            ? "GM"
            : LanguageRuntime.GetString("Campaigns.Memory.ScopePublic");

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.BeginInvoke(action);
    }

}
