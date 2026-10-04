using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using TavernDesk.App.Localization;
using TavernDesk.App.Presentation;
using TavernDesk.App.Services;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Flow;
using TavernDesk.Core.Models;
using TavernDesk.Infrastructure.Providers;

namespace TavernDesk.App.ViewModels;

public sealed class CampaignsViewModel : ViewModelBase, IDisposable
{
    private readonly ICampaignScenarioRepository _scenarios;
    private readonly ICampaignScenarioCardImporter _scenarioCards;
    private readonly ICampaignRepository _campaigns;
    private readonly ICampaignRunner _runner;
    private readonly ICharacterRepository _characters;
    private readonly ICampaignCharacterSnapshotAdapter _snapshots;
    private readonly IMemoryBankService _memoryBanks;
    private readonly IProviderProfileRepository _providers;
    private readonly IModelCatalogRepository _models;
    private readonly IModelAssignmentRepository _assignments;
    private readonly IAppSettingsRepository _settings;
    private readonly IFileDialogService _fileDialog;
    private readonly IUserInteractionService _interaction;
    private readonly ICampaignFlowEngine _flowEngine;
    private bool _disposed;
    private Campaign? _draftCampaign;
    private CampaignAggregate? _game;
    private CampaignScenario? _selectedScenario;
    private CampaignSummaryItemViewModel? _selectedCampaign;
    private CampaignFlowChoice _selectedFlow;
    private CampaignGmChoice _selectedGm;
    private CampaignUserParticipationChoice _selectedUserParticipation;
    private CampaignModelOption? _selectedGmRoute;
    private CampaignEventItemViewModel? _selectedEvent;
    private string _screen = "library";
    private string _statusText = LanguageRuntime.GetString("Campaigns.Status.Intro");
    private string _title = string.Empty;
    private string _worldSetting = string.Empty;
    private string _rules = string.Empty;
    private string _openingPrompt = string.Empty;
    private string _userPersonaName = "USER";
    private string _userPersonaDescription = string.Empty;
    private int _playerHistoryBudget = 12000;
    private int _gmHistoryBudget = 20000;
    private string _actionInput = string.Empty;
    private string _gmResolutionInput = string.Empty;
    private string _gmMaxOutputTokensText = "6000";
    private string _diceExpression = "1d20";
    private bool _isBusy;
    private readonly Dictionary<string, CampaignGenerationProgress>
        _generationProgresses = new(StringComparer.Ordinal);
    private string? _selectedGmCandidateId;
    private bool _updatingCharacterSelection;
    private CampaignGameUiState _gameUiState = CampaignGameUiState.Empty;

    public CampaignsViewModel(
        ICampaignScenarioRepository scenarios,
        ICampaignScenarioCardImporter scenarioCards,
        ICampaignRepository campaigns,
        ICampaignRunner runner,
        ICharacterRepository characters,
        ICampaignCharacterSnapshotAdapter snapshots,
        IMemoryBankService memoryBanks,
        IProviderProfileRepository providers,
        IModelCatalogRepository models,
        IModelAssignmentRepository assignments,
        IAppSettingsRepository settings,
        IFileDialogService fileDialog,
        IUserInteractionService interaction,
        IWorldbookService? worldbooks = null,
        ICampaignMemoryRepository? campaignMemories = null,
        ICampaignMemoryUpdateService? campaignMemoryUpdater = null,
        ICampaignContextPlanner? campaignContextPlanner = null,
        ICampaignFlowEngine? flowEngine = null)
    {
        _scenarios = scenarios;
        _scenarioCards = scenarioCards;
        _campaigns = campaigns;
        _runner = runner;
        _characters = characters;
        _snapshots = snapshots;
        _memoryBanks = memoryBanks;
        _providers = providers;
        _models = models;
        _assignments = assignments;
        _settings = settings;
        _fileDialog = fileDialog;
        _interaction = interaction;
        ScenarioEditor = new CampaignScenarioEditorViewModel(scenarios, worldbooks);
        ScenarioEditor.PropertyChanged += (_, args) => OnPropertyChanged(args.PropertyName);
        _flowEngine = flowEngine ?? CampaignFlowEngineFactory.CreateDefault();

        ContextPreview = new CampaignContextPreviewViewModel(
            campaignContextPlanner, scenarios, campaignMemories, _flowEngine,
            game => !_disposed && ReferenceEquals(_game, game),
            OnPreviewChanged);
        SettingsPanel = new CampaignSettingsPanelViewModel(
            campaigns, campaignMemories, campaignMemoryUpdater,
            () => _game, () => IsGame, () => IsCampaignOperationBusy,
            RunUiAsync, LoadGameAsync, value => StatusText = value,
            () => { RefreshSeatActionStates(); RaiseGameProperties(); });
        _runner.ProgressChanged += OnCampaignGenerationProgressChanged;
        FlowChoices =
        [
            new CampaignFlowChoice(
                CampaignFlowPreset.CollaborativeTable,
                LanguageRuntime.GetString("Campaigns.Flow.Collaborative"),
                LanguageRuntime.GetString("Campaigns.Flow.CollaborativeHelp")),
            new CampaignFlowChoice(
                CampaignFlowPreset.BlindSubmission,
                LanguageRuntime.GetString("Campaigns.Flow.Blind"),
                LanguageRuntime.GetString("Campaigns.Flow.BlindHelp")),
            new CampaignFlowChoice(
                CampaignFlowPreset.StrictInitiative,
                LanguageRuntime.GetString("Campaigns.Flow.Strict"),
                LanguageRuntime.GetString("Campaigns.Flow.StrictHelp"))
        ];
        GmChoices =
        [
            new CampaignGmChoice(
                CampaignGmKind.Ai,
                LanguageRuntime.GetString("Campaigns.Gm.Ai"),
                LanguageRuntime.GetString("Campaigns.Gm.AiHelp")),
            new CampaignGmChoice(
                CampaignGmKind.User,
                LanguageRuntime.GetString("Campaigns.Gm.User"),
                LanguageRuntime.GetString("Campaigns.Gm.UserHelp"))
        ];
        UserParticipationChoices =
        [
            new CampaignUserParticipationChoice(
                true,
                LanguageRuntime.GetString("Campaigns.Participation.Player"),
                LanguageRuntime.GetString("Campaigns.Participation.PlayerHelp")),
            new CampaignUserParticipationChoice(
                false,
                LanguageRuntime.GetString("Campaigns.Participation.Watch"),
                LanguageRuntime.GetString("Campaigns.Participation.WatchHelp"))
        ];
        _selectedFlow = FlowChoices[0];
        _selectedGm = GmChoices[0];
        _selectedUserParticipation = UserParticipationChoices[0];

        ImportScenarioCommand = new AsyncRelayCommand(ImportScenarioAsync);
        RestoreScenarioDraftCommand = new AsyncRelayCommand(parameter => RunUiAsync(async () =>
        {
            if (parameter is not CampaignScenarioEditDraft draft || ScenarioEditor.HasActiveEdit
                || _scenarios is not ICampaignScenarioDraftRepository repository) return;
            var current = (await Task.Run(() => repository.ListEditDraftsAsync())).FirstOrDefault(item => item.Id == draft.Id);
            if (current is null) { await RefreshRecoveryDraftsAsync(); return; }
            await ScenarioEditor.RestoreDraftAsync(current);
            ShowScreen("scenario-editor");
        }));
        DiscardScenarioDraftCommand = new AsyncRelayCommand(parameter => RunUiAsync(async () =>
        {
            if (parameter is not CampaignScenarioEditDraft draft || _scenarios is not ICampaignScenarioDraftRepository repository
                || !_interaction.ConfirmDiscardScenarioDraft(draft.Scenario.Title)) return;
            await Task.Run(() => repository.DeleteEditDraftAsync(draft.Id));
            await RefreshRecoveryDraftsAsync();
        }));
        NewScenarioCommand = new AsyncRelayCommand(NewScenarioAsync);
        EditScenarioCommand = new AsyncRelayCommand(
            parameter => EditScenarioAsync(parameter as CampaignScenario),
            parameter => parameter is CampaignScenario || SelectedScenario is not null);
        SaveScenarioCommand = new AsyncRelayCommand(SaveScenarioAsync);
        OpenScenarioLobbyCommand = new AsyncRelayCommand(
            OpenScenarioLobbyAsync,
            () => SelectedScenario is not null && !IsBusy);
        ContinueCampaignCommand = new AsyncRelayCommand(ContinueSelectedCampaignAsync, () => SelectedCampaign is not null);
        RenameCampaignCommand = new AsyncRelayCommand(RenameCampaignAsync);
        DeleteCampaignCommand = new AsyncRelayCommand(DeleteCampaignAsync);
        BackToLibraryCommand = new AsyncRelayCommand(BackToLibraryAsync);
        SaveLobbyCommand = new AsyncRelayCommand(SaveLobbyAsync);
        StartCampaignCommand = new AsyncRelayCommand(StartCampaignAsync);
        RefreshGameCommand = new AsyncRelayCommand(RefreshGameAsync);
        SubmitUserActionCommand = new AsyncRelayCommand(SubmitUserActionAsync);
        GenerateAiActionsCommand = new AsyncRelayCommand(GenerateAiActionsAsync);
        GenerateAiSeatActionCommand = new AsyncRelayCommand(
            GenerateAiSeatActionAsync);
        ResolveRoundCommand = new AsyncRelayCommand(ResolveRoundAsync);
        PreviousGmCandidateCommand = new AsyncRelayCommand(
            () => MoveGmCandidateAsync(-1),
            () => CanMoveGmCandidate(-1));
        NextGmCandidateCommand = new AsyncRelayCommand(
            () => MoveGmCandidateAsync(1),
            () => CanMoveGmCandidate(1));
        ScheduleUserJoinCommand = new AsyncRelayCommand(
            ScheduleUserJoinAsync);
        RollDiceCommand = new AsyncRelayCommand(RollDiceAsync);
        RollDicePresetCommand = new AsyncRelayCommand(RollDicePresetAsync);
        RetryEventCommand = new AsyncRelayCommand(RetryEventAsync);
        ApplySeatRouteCommand = new AsyncRelayCommand(ApplySeatRouteAsync);
        ApplyGmRouteCommand = new AsyncRelayCommand(ApplyGmRouteAsync);
        OpenGlobalPromptCommand = new AsyncRelayCommand(OpenGlobalPromptAsync);
    }

    public CampaignScenarioEditorViewModel ScenarioEditor { get; }
    public CampaignContextPreviewViewModel ContextPreview { get; }
    public CampaignSettingsPanelViewModel SettingsPanel { get; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SettingsPanel.Dispose();
        _runner.ProgressChanged -= OnCampaignGenerationProgressChanged;
    }

    private void OnPreviewChanged()
    {
        RefreshSeatActionStates();
        OnPropertyChanged(nameof(CanGenerateBlindAiActions));
        OnPropertyChanged(nameof(BlindAiActionHelpText));
        OnPropertyChanged(nameof(CanResolve));
        OnPropertyChanged(nameof(ResolveHelpText));
    }
    public Task OfferScenarioRecoveryAsync() => RefreshRecoveryDraftsAsync();

    private async Task RefreshRecoveryDraftsAsync()
    {
        RecoveryDrafts = _scenarios is ICampaignScenarioDraftRepository drafts
            ? await Task.Run(() => drafts.ListEditDraftsAsync()) : [];
        OnPropertyChanged(nameof(RecoveryDrafts));
        OnPropertyChanged(nameof(OrphanRecoveryDrafts));
    }

    public IReadOnlyList<CampaignScenarioEditDraft> RecoveryDrafts { get; private set; } = [];
    public IReadOnlyList<CampaignScenarioEditDraft> OrphanRecoveryDrafts =>
        RecoveryDrafts.Where(draft => !Scenarios.Any(scenario => scenario.Id == draft.Id)).ToArray();
    public AsyncRelayCommand RestoreScenarioDraftCommand { get; }
    public AsyncRelayCommand DiscardScenarioDraftCommand { get; }

    public ObservableCollection<CampaignScenario> Scenarios { get; } = [];
    public ObservableCollection<CampaignSummaryItemViewModel> Campaigns { get; } = [];
    public ObservableCollection<CampaignCharacterChoiceViewModel> CharacterChoices { get; } = [];
    public ObservableCollection<CampaignModelOption> ModelOptions { get; } = [];
    public ObservableCollection<CampaignSeatViewModel> Seats { get; } = [];
    public ObservableCollection<CampaignEventItemViewModel> Events { get; } = [];
    public ObservableCollection<CampaignWorldbookBindingItem>
        ScenarioWorldbookBindings => ScenarioEditor.ScenarioWorldbookBindings;
    public IReadOnlyList<CampaignFlowChoice> FlowChoices { get; }
    public IReadOnlyList<CampaignNarrativePermissionChoice>
        NarrativePermissionChoices => ScenarioEditor.NarrativePermissionChoices;
    public IReadOnlyList<CampaignGmChoice> GmChoices { get; }
    public IReadOnlyList<CampaignUserParticipationChoice>
        UserParticipationChoices { get; }

    public AsyncRelayCommand ImportScenarioCommand { get; }
    public AsyncRelayCommand NewScenarioCommand { get; }
    public AsyncRelayCommand EditScenarioCommand { get; }
    public AsyncRelayCommand SaveScenarioCommand { get; }
    public AsyncRelayCommand OpenScenarioLobbyCommand { get; }
    public AsyncRelayCommand ContinueCampaignCommand { get; }
    public AsyncRelayCommand RenameCampaignCommand { get; }
    public AsyncRelayCommand DeleteCampaignCommand { get; }
    public AsyncRelayCommand BackToLibraryCommand { get; }
    public AsyncRelayCommand SaveLobbyCommand { get; }
    public AsyncRelayCommand StartCampaignCommand { get; }
    public AsyncRelayCommand RefreshGameCommand { get; }
    public AsyncRelayCommand SubmitUserActionCommand { get; }
    public AsyncRelayCommand GenerateAiActionsCommand { get; }
    public AsyncRelayCommand GenerateAiSeatActionCommand { get; }
    public AsyncRelayCommand ResolveRoundCommand { get; }
    public AsyncRelayCommand PreviousGmCandidateCommand { get; }
    public AsyncRelayCommand NextGmCandidateCommand { get; }
    public AsyncRelayCommand ScheduleUserJoinCommand { get; }
    public AsyncRelayCommand RollDiceCommand { get; }
    public AsyncRelayCommand RollDicePresetCommand { get; }
    public AsyncRelayCommand RetryEventCommand { get; }
    public AsyncRelayCommand ApplySeatRouteCommand { get; }
    public AsyncRelayCommand ApplyGmRouteCommand { get; }
    public AsyncRelayCommand OpenGlobalPromptCommand { get; }
    public Func<GlobalPromptKey, Task>? OpenPromptSettings { get; set; }

    public bool IsLibrary => _screen == "library";
    public bool IsScenarioEditor => _screen == "scenario-editor";
    public bool IsCreatingScenario => ScenarioEditor.IsCreatingScenario;
    public string ScenarioEditorTitle => ScenarioEditor.ScenarioEditorTitle;
    public string ScenarioEditorDescription => ScenarioEditor.ScenarioEditorDescription;
    public bool IsLobby => _screen == "lobby";
    public bool IsGame => _screen == "game";
    public bool IsAiGm => SelectedGm.Value == CampaignGmKind.Ai;
    public bool IsUserGm => SelectedGm.Value == CampaignGmKind.User;
    public int SelectedAiPlayerCount =>
        CharacterChoices.Count(item => item.IsSelected);
    public string LobbyRosterText =>
        LanguageRuntime.Format(
            "Campaigns.Lobby.RosterFormat",
            UserAlsoPlayer ? 1 : 0,
            SelectedAiPlayerCount);
    public bool IsRequestReceiving =>
        _generationProgresses.Values.Any(item =>
            item.Status == CampaignGenerationStatus.Streaming);
    public string RequestProgressText => IsRequestReceiving
        ? LanguageRuntime.Format(
            "Campaigns.Request.ReceivingFormat",
            _generationProgresses.Count)
        : string.Empty;
    public string RequestReceivedTokenText => IsRequestReceiving
        ? LanguageRuntime.Format(
            "Campaigns.Request.TokensFormat",
            _generationProgresses.Values.Sum(item => item.ReceivedTokens))
        : string.Empty;
    // Memory updates run in the background and must not disable local campaign
    // navigation or seat controls.  Provider-generation commands still use
    // IsBusy to prevent duplicate foreground operations.
    public bool IsCampaignOperationBusy => IsBusy;
    public bool CanSubmitUserAction =>
        !IsCampaignOperationBusy
        && _gameUiState.UserSeatCanAct
        && !string.IsNullOrWhiteSpace(ActionInput);
    public bool CanResolve =>
        !IsCampaignOperationBusy
        && _gameUiState.ShowResolveSection
        && (IsGmCandidatePending
            ? IsSelectedGmCandidateValid
            : !(_game?.Campaign.GmKind == CampaignGmKind.Ai
                && ContextPreview.IsBlocked)
              && (_game?.Campaign.GmKind == CampaignGmKind.Ai
                  || !string.IsNullOrWhiteSpace(GmResolutionInput)));
    public bool HasUserSeat => _gameUiState.HasUserSeat;
    public bool HasPendingUserJoin => _gameUiState.HasPendingUserJoin;
    public bool ShowUserJoinSection => !HasUserSeat;
    public bool CanScheduleUserJoin =>
        !IsCampaignOperationBusy && _gameUiState.CanScheduleUserJoin;
    public bool ShowUserActionSection =>
        _gameUiState.ShowUserActionSection;
    public bool ShowBlindAiAction =>
        _gameUiState.ShowBlindAiAction;
    public bool CanGenerateBlindAiActions =>
        !IsCampaignOperationBusy
        && _gameUiState.CanGenerateBlindAiActions
        && !ContextPreview.IsBlocked;
    public bool ShowResolveSection =>
        _gameUiState.ShowResolveSection;
    public string CurrentStepTitle => _gameUiState.CurrentStepTitle;
    public string CurrentStepDescription =>
        _gameUiState.CurrentStepDescription;
    public string CurrentStepProgressText =>
        _gameUiState.CurrentStepProgressText;
    public string GmModeText => _gameUiState.GmModeText;
    public string ParticipationModeText =>
        _gameUiState.ParticipationModeText;
    public string UserActionHelpText =>
        string.IsNullOrWhiteSpace(ActionInput)
        && _gameUiState.UserSeatCanAct
            ? LanguageRuntime.GetString("Campaigns.UserAction.Required")
            : _gameUiState.UserActionHelpText;
    public string BlindAiActionHelpText =>
        ContextPreview.IsBlocked
            ? ContextPreview.BlockedHelpText
            : _gameUiState.BlindAiActionHelpText;
    public string ResolveButtonText =>
        _game?.Campaign.GmKind == CampaignGmKind.Ai
            ? IsGmCandidatePending
                ? LanguageRuntime.GetString("Campaigns.Resolve.CommitCandidate")
                : AiGmResolutionNeedsRetry
                ? LanguageRuntime.GetString("Campaigns.Resolve.RetryAi")
                : LanguageRuntime.GetString("Campaigns.Resolve.Ai")
            : LanguageRuntime.GetString("Campaigns.Resolve.User");
    public string ResolveHelpText =>
        IsGmCandidatePending
            ? IsSelectedGmCandidateValid
                ? LanguageRuntime.GetString("Campaigns.Resolve.CandidateValid")
                : LanguageRuntime.GetString("Campaigns.Resolve.CandidateInvalid")
        :
        _game?.Campaign.GmKind == CampaignGmKind.Ai
        && ContextPreview.IsBlocked
            ? ContextPreview.BlockedHelpText
        : _game?.Campaign.GmKind == CampaignGmKind.User
        && _gameUiState.ShowResolveSection
        && string.IsNullOrWhiteSpace(GmResolutionInput)
            ? LanguageRuntime.GetString("Campaigns.Resolve.InputRequired")
            : _gameUiState.ResolveHelpText;
    public string ScheduleUserJoinButtonText =>
        HasPendingUserJoin
            ? LanguageRuntime.GetString("Campaigns.UserJoin.Pending")
            : LanguageRuntime.GetString("Campaigns.UserJoin.Action");
    public string ScheduleUserJoinHelpText =>
        HasPendingUserJoin
            ? LanguageRuntime.GetString("Campaigns.UserJoin.PendingHelp")
            : LanguageRuntime.GetString("Campaigns.UserJoin.Help");
    public string GameTitle => _game?.Campaign.Title ?? string.Empty;
    public string GamePhaseText => _game is null
        ? string.Empty
        : LanguageRuntime.Format(
            "Campaigns.Game.MetaFormat",
            _game.Campaign.CurrentRound,
            FlowName(_game.Campaign.FlowPreset),
            PhaseName(_game.Campaign.Phase));
    public string SaveStateText => _game is null
        ? string.Empty
        : LanguageRuntime.Format("Campaigns.Game.SavedFormat", _game.Campaign.StateVersion);
    private bool AiGmResolutionNeedsRetry =>
        _game?.Campaign.GmKind == CampaignGmKind.Ai
        && GetGmCandidates()
            .LastOrDefault()
            ?.GenerationStatus is (
                CampaignGenerationStatus.Failed
            or CampaignGenerationStatus.Interrupted);

    private bool IsGmCandidatePending =>
        _game?.Campaign.GmKind == CampaignGmKind.Ai
        && _game.Campaign.Phase == CampaignPhase.ReadyForResolution
        && _flowEngine.PlanResolution(_game).CanCommit;

    private IReadOnlyList<CampaignEvent> GetGmCandidates() =>
        _game is null
            ? Array.Empty<CampaignEvent>()
            : _game.Events
                .Where(item => _flowEngine.PlanResolution(_game)
                    .CandidateResolutionIds.Contains(item.Id, StringComparer.Ordinal))
                .OrderBy(item => item.SequenceNo)
                .ToArray();

    private CampaignEvent? GetSelectedGmCandidate()
    {
        var candidates = GetGmCandidates();
        if (candidates.Count == 0)
        {
            return null;
        }

        return candidates.FirstOrDefault(item => item.Id == _selectedGmCandidateId)
               ?? candidates[^1];
    }

    private int SelectedGmCandidateIndex(
        IReadOnlyList<CampaignEvent> candidates)
    {
        for (var index = 0; index < candidates.Count; index++)
        {
            if (candidates[index].Id == _selectedGmCandidateId)
            {
                return index;
            }
        }

        return candidates.Count - 1;
    }

    public bool HasGmCandidateNavigation =>
        IsGmCandidatePending && GetGmCandidates().Count > 1;

    public string GmCandidateNavigationLabel
    {
        get
        {
            if (!HasGmCandidateNavigation)
            {
                return string.Empty;
            }

            var candidates = GetGmCandidates();
            if (candidates.Count == 0)
            {
                return string.Empty;
            }

            var index = SelectedGmCandidateIndex(candidates);
            return $"{index + 1}/{candidates.Count}";
        }
    }

    public bool IsSelectedGmCandidateValid =>
        GetSelectedGmCandidate()?.GenerationStatus
            == CampaignGenerationStatus.Completed
        && GetSelectedGmCandidate()?.EndReason == CampaignEndReason.Normal;

    public CampaignScenario? SelectedScenario
    {
        get => _selectedScenario;
        set
        {
            if (SetProperty(ref _selectedScenario, value))
            {
                EditScenarioCommand.RaiseCanExecuteChanged();
                OpenScenarioLobbyCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public CampaignSummaryItemViewModel? SelectedCampaign
    {
        get => _selectedCampaign;
        set
        {
            if (SetProperty(ref _selectedCampaign, value)) ContinueCampaignCommand.RaiseCanExecuteChanged();
        }
    }

    public CampaignEventItemViewModel? SelectedEvent
    {
        get => _selectedEvent;
        set => SetProperty(ref _selectedEvent, value);
    }

    public CampaignFlowChoice SelectedFlow
    {
        get => _selectedFlow;
        set => SetProperty(ref _selectedFlow, value);
    }

    public CampaignGmChoice SelectedGm
    {
        get => _selectedGm;
        set
        {
            if (SetProperty(ref _selectedGm, value))
            {
                OnPropertyChanged(nameof(IsAiGm));
                OnPropertyChanged(nameof(IsUserGm));
            }
        }
    }

    public CampaignUserParticipationChoice SelectedUserParticipation
    {
        get => _selectedUserParticipation;
        set
        {
            if (SetProperty(ref _selectedUserParticipation, value))
            {
                OnPropertyChanged(nameof(UserAlsoPlayer));
                OnPropertyChanged(nameof(LobbyRosterText));
            }
        }
    }

    public CampaignModelOption? SelectedGmRoute
    {
        get => _selectedGmRoute;
        set => SetProperty(ref _selectedGmRoute, value);
    }

    public string GmMaxOutputTokensText
    {
        get => _gmMaxOutputTokensText;
        set => SetProperty(ref _gmMaxOutputTokensText, value);
    }

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public string WorldSetting
    {
        get => _worldSetting;
        set => SetProperty(ref _worldSetting, value);
    }

    public string Rules
    {
        get => _rules;
        set => SetProperty(ref _rules, value);
    }

    public string OpeningPrompt
    {
        get => _openingPrompt;
        set => SetProperty(ref _openingPrompt, value);
    }

    public string ScenarioTitle
    {
        get => ScenarioEditor.ScenarioTitle;
        set => ScenarioEditor.ScenarioTitle = value;
    }

    public string ScenarioSummary
    {
        get => ScenarioEditor.ScenarioSummary;
        set => ScenarioEditor.ScenarioSummary = value;
    }

    public string ScenarioWorldSetting
    {
        get => ScenarioEditor.ScenarioWorldSetting;
        set => ScenarioEditor.ScenarioWorldSetting = value;
    }

    public string ScenarioPublicRules
    {
        get => ScenarioEditor.ScenarioPublicRules;
        set => ScenarioEditor.ScenarioPublicRules = value;
    }

    public string ScenarioGmInstructions
    {
        get => ScenarioEditor.ScenarioGmInstructions;
        set => ScenarioEditor.ScenarioGmInstructions = value;
    }

    public string ScenarioOpeningSetup
    {
        get => ScenarioEditor.ScenarioOpeningSetup;
        set => ScenarioEditor.ScenarioOpeningSetup = value;
    }

    public string ScenarioOpeningNarration
    {
        get => ScenarioEditor.ScenarioOpeningNarration;
        set => ScenarioEditor.ScenarioOpeningNarration = value;
    }

    public string ScenarioLegacyExamplesArchive
    {
        get => ScenarioEditor.ScenarioLegacyExamplesArchive;
        set => ScenarioEditor.ScenarioLegacyExamplesArchive = value;
    }

    public string UserPersonaName
    {
        get => _userPersonaName;
        set => SetProperty(ref _userPersonaName, value);
    }

    public string UserPersonaDescription
    {
        get => _userPersonaDescription;
        set => SetProperty(ref _userPersonaDescription, value);
    }

    public bool UserAlsoPlayer
    {
        get => SelectedUserParticipation.UserAlsoPlayer;
        set => SelectedUserParticipation =
            UserParticipationChoices.Single(item =>
                item.UserAlsoPlayer == value);
    }

    public int PlayerHistoryBudget
    {
        get => _playerHistoryBudget;
        set => SetProperty(ref _playerHistoryBudget, value);
    }

    public int GmHistoryBudget
    {
        get => _gmHistoryBudget;
        set => SetProperty(ref _gmHistoryBudget, value);
    }

    public string ActionInput
    {
        get => _actionInput;
        set
        {
            if (SetProperty(ref _actionInput, value))
            {
                OnPropertyChanged(nameof(CanSubmitUserAction));
                OnPropertyChanged(nameof(UserActionHelpText));
            }
        }
    }

    public string GmResolutionInput
    {
        get => _gmResolutionInput;
        set
        {
            if (SetProperty(ref _gmResolutionInput, value))
            {
                OnPropertyChanged(nameof(CanResolve));
                OnPropertyChanged(nameof(ResolveHelpText));
            }
        }
    }

    public string DiceExpression
    {
        get => _diceExpression;
        set => SetProperty(ref _diceExpression, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OpenScenarioLobbyCommand.RaiseCanExecuteChanged();
                RefreshSeatActionStates();
                RaiseGameProperties();
            }
        }
    }

    public async Task LoadAsync()
    {
        try
        {
            await LoadReferenceDataAsync();
            await RefreshLibraryAsync();
            if (_game is not null)
            {
                await LoadGameAsync(_game.Campaign.Id);
            }
        }
        catch (Exception exception)
        {
            StatusText = LanguageRuntime.ErrorMessage(exception);
        }
    }

    public CampaignNarrativePermissionChoice ScenarioNewNpcPermission
    {
        get => ScenarioEditor.ScenarioNewNpcPermission;
        set => ScenarioEditor.ScenarioNewNpcPermission = value;
    }

    public CampaignNarrativePermissionChoice ScenarioRelationshipChangePermission
    {
        get => ScenarioEditor.ScenarioRelationshipChangePermission;
        set => ScenarioEditor.ScenarioRelationshipChangePermission = value;
    }

    public CampaignNarrativePermissionChoice ScenarioIndependentPlotPermission
    {
        get => ScenarioEditor.ScenarioIndependentPlotPermission;
        set => ScenarioEditor.ScenarioIndependentPlotPermission = value;
    }

    private async Task LoadReferenceDataAsync()
    {
        var charactersTask = _characters.ListAsync();
        var providersTask = _providers.ListAsync();
        var assignmentTask = _assignments.GetAsync(ModelFunctionKind.Chat);
        var personaNameTask = _settings.GetAsync("persona.name");
        var personaDescriptionTask = _settings.GetAsync("persona.description");
        await Task.WhenAll(
            charactersTask,
            providersTask,
            assignmentTask,
            personaNameTask,
            personaDescriptionTask);

        ModelOptions.Clear();
        foreach (var provider in providersTask.Result.Where(item => item.IsEnabled))
        {
            var models = (await _models.ListAsync(provider.Id))
                .Where(model => model.ModelKind is ModelCatalogKind.Chat
                    or ModelCatalogKind.Custom)
                .ToArray();
            if (provider.AdapterKind == ProviderAdapterKind.GrokCli
                && models.Length == 0)
            {
                ModelOptions.Add(new CampaignModelOption(
                    provider.Id,
                    provider.Name,
                    GrokCliProviderGateway.DefaultModelId,
                    LanguageRuntime.GetString("Campaigns.Model.SubscriptionDefault"),
                    131072,
                    4096));
            }
            else
            {
                foreach (var model in models.OrderBy(item => item.DisplayName))
                {
                    ModelOptions.Add(new CampaignModelOption(
                        provider.Id,
                        provider.Name,
                        model.ModelId,
                        string.IsNullOrWhiteSpace(model.DisplayName)
                            ? model.ModelId
                            : model.DisplayName,
                        model.ContextLimit,
                        model.MaxOutputTokens));
                }
            }
        }

        var defaultRoute = FindRoute(
                               assignmentTask.Result?.ProviderId,
                               assignmentTask.Result?.ModelId)
                           ?? ModelOptions.FirstOrDefault();
        SelectedGmRoute ??= defaultRoute;
        foreach (var choice in CharacterChoices)
        {
            choice.PropertyChanged -= OnCharacterChoicePropertyChanged;
        }

        CharacterChoices.Clear();
        foreach (var character in charactersTask.Result.OrderBy(item => item.Name))
        {
            var choice = new CampaignCharacterChoiceViewModel(character)
            {
                SelectedRoute = defaultRoute
            };
            choice.PropertyChanged += OnCharacterChoicePropertyChanged;
            CharacterChoices.Add(choice);
        }
        RefreshCharacterSelectionState();

        UserPersonaName = personaNameTask.Result ?? "USER";
        UserPersonaDescription = personaDescriptionTask.Result ?? string.Empty;
    }

    private async Task RefreshLibraryAsync()
    {
        var preferredScenarioId = SelectedScenario?.Id;
        var scenariosTask = _scenarios.ListAsync();
        var campaignsTask = _campaigns.ListAsync();
        await Task.WhenAll(scenariosTask, campaignsTask);
        Scenarios.Clear();
        foreach (var scenario in scenariosTask.Result)
        {
            Scenarios.Add(scenario);
        }

        Campaigns.Clear();
        foreach (var campaign in campaignsTask.Result)
        {
            Campaigns.Add(new CampaignSummaryItemViewModel(campaign));
        }

        SelectedScenario = Scenarios.FirstOrDefault(item =>
                              item.Id == preferredScenarioId)
                          ?? Scenarios.FirstOrDefault();
        SelectedCampaign = Campaigns.FirstOrDefault();
        await RefreshRecoveryDraftsAsync();
    }

    private async Task ImportScenarioAsync()
    {
        var sourcePath = _fileDialog.PickCampaignScenarioCard();
        if (sourcePath is null)
        {
            return;
        }

        await RunUiAsync(async () =>
        {
            var result = await _scenarioCards.ImportAsync(sourcePath);
            await RefreshLibraryAsync();
            SelectedScenario = Scenarios.FirstOrDefault(item =>
                item.Id == result.Scenario.Id);
            StatusText = result.Warnings.Count == 0
                ? LanguageRuntime.Format("Campaigns.Scenario.ImportedFormat", result.Scenario.Title)
                : LanguageRuntime.Format(
                    "Campaigns.Scenario.ImportedWarningsFormat",
                    result.Scenario.Title,
                    string.Join(
                        LanguageRuntime.GetString("Common.ListSeparator"),
                        LanguageRuntime.LocalizeDiagnostics(
                            result.Warnings,
                            "Campaigns.Scenario.WarningSummaryFormat")));
        });
    }

    private async Task NewScenarioAsync()
    {
        await RunUiAsync(async () =>
        {
            SelectedScenario = new CampaignScenario();
            await ScenarioEditor.LoadAsync(SelectedScenario, isCreating: true);
            ShowScreen("scenario-editor");
            StatusText = LanguageRuntime.GetString("Campaigns.Scenario.FillHint");
        });
    }

    private async Task EditScenarioAsync(CampaignScenario? scenarioToEdit = null)
    {
        if (scenarioToEdit is not null) SelectedScenario = scenarioToEdit;
        if (SelectedScenario is not { } selected)
        {
            StatusText = LanguageRuntime.GetString("Campaigns.Scenario.Select");
            return;
        }

        if (RecoveryDrafts.Any(draft => draft.Id == selected.Id))
        {
            StatusText = LanguageRuntime.GetString("Recovery.EditHint");
            return;
        }

        await RunUiAsync(async () =>
        {
            var scenario = await _scenarios.GetAsync(selected.Id)
                           ?? throw new InvalidOperationException(
                               LanguageRuntime.GetString("Campaigns.Scenario.Missing"));
            await ScenarioEditor.LoadAsync(scenario);
            ShowScreen("scenario-editor");
            StatusText = LanguageRuntime.GetString("Campaigns.Scenario.EditHint");
        });
    }

    private async Task SaveScenarioAsync()
    {
        await RunUiAsync(async () =>
        {
            var scenario = await ScenarioEditor.SaveAsync();
            await RefreshLibraryAsync();
            SelectedScenario = Scenarios.FirstOrDefault(item => item.Id == scenario.Id);
            ScenarioEditor.EndEdit();
            ShowScreen("library");
            StatusText = LanguageRuntime.Format("Campaigns.Scenario.SavedFormat", scenario.Title);
        });
    }

    private async Task OpenScenarioLobbyAsync()
    {
        if (SelectedScenario is null)
        {
            StatusText = LanguageRuntime.GetString("Campaigns.Scenario.Select");
            return;
        }

        await RunUiAsync(async () =>
        {
            _draftCampaign = null;
            ResetCharacterChoices();
            ApplyScenarioToLobby(SelectedScenario);
            Title = await NextCampaignTitleAsync(SelectedScenario);
            ShowScreen("lobby");
            StatusText = LanguageRuntime.GetString("Campaigns.Lobby.Intro");
        });
    }

    private async Task ContinueSelectedCampaignAsync()
    {
        if (SelectedCampaign is null)
        {
            StatusText = LanguageRuntime.GetString("Campaigns.Game.Select");
            return;
        }

        await RunUiAsync(async () =>
        {
            var aggregate = await _campaigns.GetAsync(SelectedCampaign.Id)
                            ?? throw new InvalidOperationException(
                                LanguageRuntime.GetString("Campaigns.Game.Missing"));
            if (aggregate.Campaign.Status == CampaignStatus.Draft)
            {
                await LoadDraftIntoLobbyAsync(aggregate);
                StatusText =
                    LanguageRuntime.GetString("Campaigns.Lobby.DraftLoaded");
            }
            else
            {
                await LoadGameAsync(aggregate.Campaign.Id);
                StatusText =
                    LanguageRuntime.GetString("Campaigns.Game.Loaded");
            }
        });
    }

    private async Task RenameCampaignAsync(object? parameter)
    {
        if (parameter is not CampaignSummaryItemViewModel selected)
        {
            StatusText = LanguageRuntime.GetString("Campaigns.Game.RenameSelect");
            return;
        }

        var edited = await _interaction.EditTextAsync(
            LanguageRuntime.GetString("Campaigns.Game.RenameTitle"),
            LanguageRuntime.GetString("Campaigns.Game.RenamePrompt"),
            selected.Title);
        if (edited is null)
        {
            return;
        }

        var normalized = edited.Trim();
        if (normalized.Length == 0)
        {
            _interaction.ShowWarning(
                LanguageRuntime.GetString("Campaigns.Game.RenameCannot"),
                LanguageRuntime.GetString("Campaigns.Game.NameRequired"));
            return;
        }

        await RunUiAsync(async () =>
        {
            await _campaigns.UpdateTitleAsync(selected.Id, normalized);
            await RefreshLibraryAsync();
            SelectedCampaign = Campaigns.FirstOrDefault(item =>
                item.Id == selected.Id);
            StatusText = LanguageRuntime.Format("Campaigns.Game.RenamedFormat", normalized);
        });
    }

    private async Task DeleteCampaignAsync(object? parameter)
    {
        if (parameter is not CampaignSummaryItemViewModel selected)
        {
            StatusText = LanguageRuntime.GetString("Campaigns.Game.DeleteSelect");
            return;
        }

        await RunUiAsync(async () =>
        {
            var aggregate = await _campaigns.GetAsync(selected.Id);
            if (aggregate is null)
            {
                await RefreshLibraryAsync();
                StatusText = LanguageRuntime.GetString("Campaigns.Game.AlreadyMissing");
                return;
            }

            if (!_interaction.ConfirmCampaignDeletion(
                    aggregate.Campaign.Title,
                    aggregate.Events.Count))
            {
                return;
            }

            await _campaigns.DeleteAsync(selected.Id);
            if (string.Equals(
                    _game?.Campaign.Id,
                    selected.Id,
                    StringComparison.Ordinal))
            {
                _game = null;
                _gameUiState = CampaignGameUiState.Empty;
                Seats.Clear();
                Events.Clear();
            }

            await RefreshLibraryAsync();
            StatusText = LanguageRuntime.Format(
                "Campaigns.Game.DeletedFormat",
                aggregate.Campaign.Title,
                aggregate.Events.Count);
        });
    }

    private async Task BackToLibraryAsync()
    {
        if (!await ConfirmCanLeaveAsync())
        {
            return;
        }

        await RunUiAsync(async () =>
        {
            var wasEditingScenario = IsScenarioEditor;
            var wasCreatingScenario = IsCreatingScenario;
            await ScenarioEditor.DiscardDraftAsync();
            await RefreshLibraryAsync();
            ShowScreen("library");
            StatusText = wasEditingScenario
                ? wasCreatingScenario
                    ? LanguageRuntime.GetString("Campaigns.Scenario.NewCancelled")
                    : LanguageRuntime.GetString("Campaigns.Scenario.EditCancelled")
                : LanguageRuntime.GetString("Campaigns.Library.AllSaved");
        });
    }

    private async Task SaveLobbyAsync()
    {
        await RunUiAsync(async () =>
        {
            await SaveLobbyCoreAsync();
            await RefreshLibraryAsync();
            StatusText = LanguageRuntime.GetString("Campaigns.Lobby.DraftSaved");
        });
    }

    private async Task StartCampaignAsync()
    {
        await RunUiAsync(async () =>
        {
            var campaign = await SaveLobbyCoreAsync();
            var started = await _runner.StartAsync(campaign.Id);
            await LoadGameAsync(started.Campaign.Id);
            StatusText = LanguageRuntime.GetString("Campaigns.Game.Started");
        });
    }

    private async Task<Campaign> SaveLobbyCoreAsync()
    {
        if (SelectedScenario is null)
        {
            throw new InvalidOperationException(
                LanguageRuntime.GetString("Campaigns.Lobby.NoScenario"));
        }

        var selectedCharacters = CharacterChoices
            .Where(item => item.IsSelected)
            .ToArray();
        if (selectedCharacters.Length > 4)
        {
            throw new InvalidOperationException(
                LanguageRuntime.GetString("Campaigns.Lobby.MaxAi"));
        }

        if (!UserAlsoPlayer && selectedCharacters.Length == 0)
        {
            throw new InvalidOperationException(
                LanguageRuntime.GetString("Campaigns.Lobby.NeedPlayer"));
        }

        if (selectedCharacters.Any(item => item.SelectedRoute is null))
        {
            throw new InvalidOperationException(
                LanguageRuntime.GetString("Campaigns.Lobby.AiModelRequired"));
        }

        if (SelectedGm.Value == CampaignGmKind.Ai && SelectedGmRoute is null)
        {
            throw new InvalidOperationException(
                LanguageRuntime.GetString("Campaigns.Lobby.GmModelRequired"));
        }

        var campaign = _draftCampaign ?? new Campaign
        {
            StoryId = SelectedScenario.Id
        };
        campaign.Title = Title.Trim();
        campaign.WorldSetting = WorldSetting.Trim();
        campaign.Rules = Rules.Trim();
        campaign.OpeningPrompt = OpeningPrompt.Trim();
        campaign.GmInstructions = SelectedScenario.GmInstructions.Trim();
        campaign.NewNpcPermission = SelectedScenario.NewNpcPermission;
        campaign.RelationshipChangePermission =
            SelectedScenario.RelationshipChangePermission;
        campaign.IndependentPlotPermission =
            SelectedScenario.IndependentPlotPermission;
        campaign.NarrativeStateJson = "{}";
        campaign.GmKind = SelectedGm.Value;
        campaign.UserAlsoPlayer = UserAlsoPlayer;
        campaign.FlowPreset = SelectedFlow.Value;
        campaign.UserPersonaName = string.IsNullOrWhiteSpace(UserPersonaName)
            ? "USER"
            : UserPersonaName.Trim();
        campaign.UserPersonaDescription = UserPersonaDescription.Trim();
        campaign.PlayerHistoryBudget = Math.Max(512, PlayerHistoryBudget);
        campaign.GmHistoryBudget = Math.Max(512, GmHistoryBudget);
        if (SelectedGmRoute is not null)
        {
            ApplyRoute(campaign, SelectedGmRoute);
        }

        var participants = new List<CampaignParticipant>();
        var sortIndex = 0;
        if (UserAlsoPlayer)
        {
            participants.Add(new CampaignParticipant
            {
                CampaignId = campaign.Id,
                Kind = CampaignParticipantKind.User,
                SortIndex = sortIndex++,
                DisplayName = campaign.UserPersonaName,
                PersonaSnapshotJson = JsonSerializer.Serialize(new
                {
                    name = campaign.UserPersonaName,
                    description = campaign.UserPersonaDescription
                })
            });
        }

        foreach (var choice in selectedCharacters)
        {
            var memory = choice.IncludeMemory
                ? await _memoryBanks.GetBodyAsync(choice.Character.Id)
                : null;
            var snapshot = _snapshots.Create(
                choice.Character,
                memory,
                choice.IncludeMemory,
                includeOriginalWorldKnowledge: false);
            var route = choice.SelectedRoute!;
            participants.Add(new CampaignParticipant
            {
                CampaignId = campaign.Id,
                Kind = CampaignParticipantKind.Ai,
                SortIndex = sortIndex++,
                SourceCharacterId = choice.Character.Id,
                DisplayName = choice.Character.Name,
                CharacterSnapshotJson = snapshot.CharacterSnapshotJson,
                MemorySnapshot = snapshot.MemorySnapshot,
                OriginalWorldKnowledgeSnapshot =
                    snapshot.OriginalWorldKnowledgeSnapshot,
                ProviderId = route.ProviderId,
                ModelId = route.ModelId,
                ContextLimit = route.ContextLimit,
                MaxOutputTokens = Math.Min(route.MaxOutputTokens, 4096),
                Temperature = 0.8,
                TopP = 1
            });
        }

        await _campaigns.SaveDraftAsync(campaign, participants);
        _draftCampaign = campaign;
        return campaign;
    }

    private async Task LoadDraftIntoLobbyAsync(CampaignAggregate aggregate)
    {
        _draftCampaign = aggregate.Campaign;
        SelectedScenario = Scenarios.FirstOrDefault(item =>
                               item.Id == aggregate.Campaign.StoryId)
                           ?? await _scenarios.GetAsync(aggregate.Campaign.StoryId);
        Title = aggregate.Campaign.Title;
        WorldSetting = aggregate.Campaign.WorldSetting;
        Rules = aggregate.Campaign.Rules;
        OpeningPrompt = aggregate.Campaign.OpeningPrompt;
        SelectedFlow = FlowChoices.Single(item =>
            item.Value == aggregate.Campaign.FlowPreset);
        SelectedGm = GmChoices.Single(item =>
            item.Value == aggregate.Campaign.GmKind);
        SelectedGmRoute = FindRoute(
            aggregate.Campaign.GmProviderId,
            aggregate.Campaign.GmModelId);
        UserAlsoPlayer = aggregate.Campaign.UserAlsoPlayer;
        UserPersonaName = aggregate.Campaign.UserPersonaName;
        UserPersonaDescription = aggregate.Campaign.UserPersonaDescription;
        PlayerHistoryBudget = aggregate.Campaign.PlayerHistoryBudget;
        GmHistoryBudget = aggregate.Campaign.GmHistoryBudget;
        ResetCharacterChoices();
        foreach (var participant in aggregate.Participants.Where(item =>
                     item.Kind == CampaignParticipantKind.Ai))
        {
            var choice = CharacterChoices.FirstOrDefault(item =>
                item.Character.Id == participant.SourceCharacterId);
            if (choice is null)
            {
                continue;
            }

            choice.IsSelected = true;
            choice.IncludeMemory = !string.IsNullOrWhiteSpace(participant.MemorySnapshot);
            choice.SelectedRoute = FindRoute(
                                       participant.ProviderId,
                                       participant.ModelId)
                                   ?? choice.SelectedRoute;
        }

        ShowScreen("lobby");
    }

    private async Task RefreshGameAsync()
    {
        if (_game is null)
        {
            return;
        }

        await RunUiAsync(async () =>
        {
            await LoadGameAsync(_game.Campaign.Id);
            StatusText = LanguageRuntime.GetString("Campaigns.Game.Reloaded");
        });
    }

    private async Task SubmitUserActionAsync()
    {
        if (_game is null)
        {
            return;
        }

        await RunUiAsync(async () =>
        {
            await _runner.SubmitUserActionAsync(_game.Campaign.Id, ActionInput);
            ActionInput = string.Empty;
            await LoadGameAsync(_game.Campaign.Id);
            StatusText = LanguageRuntime.GetString("Campaigns.Action.UserSaved");
        });
    }

    private async Task GenerateAiActionsAsync()
    {
        if (_game is null)
        {
            return;
        }

        await RunUiAsync(async () =>
        {
            var results = await _runner.GenerateAiActionsAsync(_game.Campaign.Id);
            await LoadGameAsync(_game.Campaign.Id);
            var failures = results.Count(item =>
                item.GenerationStatus != CampaignGenerationStatus.Completed);
            StatusText = failures == 0
                ? LanguageRuntime.Format("Campaigns.Action.BlindDoneFormat", results.Count)
                : LanguageRuntime.Format(
                    "Campaigns.Action.BlindPartialFormat",
                    results.Count,
                    failures);
        });
    }

    private async Task GenerateAiSeatActionAsync(object? parameter)
    {
        if (_game is null
            || parameter is not CampaignSeatViewModel
            {
                IsAi: true,
                CanGenerateAction: true
            } seat)
        {
            return;
        }

        await RunUiAsync(async () =>
        {
            var wasRetry = seat.IsRetryAction;
            var result = wasRetry
                ? await _runner.RetryAiActionAsync(
                    _game.Campaign.Id,
                    seat.RetryEventId
                    ?? throw new InvalidOperationException(
                        LanguageRuntime.GetString("Campaigns.Action.NoRetryRecord")))
                : await _runner.GenerateAiActionAsync(
                    _game.Campaign.Id,
                    seat.Id);
            await LoadGameAsync(_game.Campaign.Id);
            StatusText = result.GenerationStatus
                         == CampaignGenerationStatus.Completed
                ? wasRetry
                    ? LanguageRuntime.Format("Campaigns.Action.RetrySucceededFormat", seat.Name)
                    : LanguageRuntime.Format("Campaigns.Action.LockedFormat", seat.Name)
                : LanguageRuntime.Format("Campaigns.Action.StillIncompleteFormat", seat.Name);
        });
    }

    private async Task ResolveRoundAsync()
    {
        if (_game is null)
        {
            return;
        }

        await RunUiAsync(async () =>
        {
            CampaignEvent resolution;
            var committingCandidate = IsGmCandidatePending;
            if (committingCandidate)
            {
                var candidate = GetSelectedGmCandidate()
                                ?? throw new InvalidOperationException(
                                    LanguageRuntime.GetString("Campaigns.Resolve.NoCandidate"));
                resolution = await _runner.CommitGmResolutionCandidateAsync(
                    _game.Campaign.Id,
                    candidate.Id);
            }
            else if (_game.Campaign.GmKind == CampaignGmKind.User)
            {
                resolution = await _runner.SubmitUserGmResolutionAsync(
                    _game.Campaign.Id,
                    GmResolutionInput);
                GmResolutionInput = string.Empty;
            }
            else
            {
                resolution =
                    await _runner.GenerateGmResolutionAsync(_game.Campaign.Id);
            }

            await LoadGameAsync(_game.Campaign.Id);
            var waitingForGmCandidate =
                _game.Campaign.GmKind == CampaignGmKind.Ai
                && IsGmCandidatePending;
            StatusText = resolution.GenerationStatus
                         == CampaignGenerationStatus.Completed
                 ? committingCandidate
                     ? LanguageRuntime.GetString("Campaigns.Resolve.CandidateCommitted")
                     : waitingForGmCandidate
                         ? LanguageRuntime.GetString("Campaigns.Resolve.RetrySucceeded")
                     : LanguageRuntime.GetString("Campaigns.Resolve.Saved")
                : resolution.EndReason == CampaignEndReason.ProtocolViolation
                    ? LanguageRuntime.GetString("Campaigns.Resolve.ProtocolViolation")
                    : resolution.EndReason
                      == CampaignEndReason.NarrativeAuthorityViolation
                        ? LanguageRuntime.GetString("Campaigns.Resolve.AuthorityViolation")
                    : LanguageRuntime.Format(
                        "Campaigns.Resolve.IncompleteFormat",
                        EndReasonName(resolution.EndReason));
        });
    }

    private async Task ScheduleUserJoinAsync()
    {
        if (_game is null || !CanScheduleUserJoin)
        {
            return;
        }

        await RunUiAsync(async () =>
        {
            await _campaigns.ScheduleUserJoinAsync(
                _game.Campaign.Id,
                _game.Campaign.StateVersion,
                string.IsNullOrWhiteSpace(_game.Campaign.UserPersonaName)
                    ? "USER"
                    : _game.Campaign.UserPersonaName.Trim(),
                JsonSerializer.Serialize(new
                {
                    name = string.IsNullOrWhiteSpace(
                        _game.Campaign.UserPersonaName)
                        ? "USER"
                        : _game.Campaign.UserPersonaName.Trim(),
                    description =
                        _game.Campaign.UserPersonaDescription.Trim()
                }));
            await LoadGameAsync(_game.Campaign.Id);
            StatusText = LanguageRuntime.GetString("Campaigns.UserJoin.Scheduled");
        });
    }

    private Task RollDicePresetAsync(object? parameter)
    {
        if (parameter is string expression && !string.IsNullOrWhiteSpace(expression))
        {
            DiceExpression = expression;
        }

        return RollDiceAsync();
    }

    private async Task RollDiceAsync()
    {
        if (_game is null)
        {
            return;
        }

        await RunUiAsync(async () =>
        {
            var actor = _game.Participants.FirstOrDefault(item =>
                            item.Kind == CampaignParticipantKind.User)?.Id
                        ?? "gm:user";
            var roll = await _runner.RollDiceAsync(
                _game.Campaign.Id,
                actor,
                DiceExpression);
            await LoadGameAsync(_game.Campaign.Id);
            StatusText = LanguageRuntime.Format("Campaigns.Dice.ResultFormat", roll.Content);
        });
    }

    private async Task RetryEventAsync(object? parameter)
    {
        if (_game is null
            || parameter is not CampaignEventItemViewModel { CanRetry: true } item)
        {
            return;
        }

        await RunUiAsync(async () =>
        {
            var retry = await _runner.RetryAiActionAsync(
                _game.Campaign.Id,
                item.Event.Id);
            await LoadGameAsync(_game.Campaign.Id);
            StatusText = retry.GenerationStatus == CampaignGenerationStatus.Completed
                ? LanguageRuntime.GetString("Campaigns.Action.RetrySuccess")
                : LanguageRuntime.GetString("Campaigns.Action.RetryIncomplete");
        });
    }

    private async Task ApplySeatRouteAsync(object? parameter)
    {
        if (_game is null
            || parameter is not CampaignSeatViewModel seat
            || seat.Participant.Kind != CampaignParticipantKind.Ai
            || seat.SelectedRoute is null)
        {
            return;
        }

        await RunUiAsync(async () =>
        {
            await _campaigns.UpdateParticipantRouteAsync(
                _game.Campaign.Id,
                seat.Id,
                seat.SelectedRoute.ToRoute());
            await LoadGameAsync(_game.Campaign.Id);
            StatusText = LanguageRuntime.Format(
                "Campaigns.Seat.ModelChangedFormat",
                seat.Name,
                seat.SelectedRoute.DisplayLabel);
        });
    }

    private async Task ApplyGmRouteAsync()
    {
        if (_game is null || SelectedGmRoute is null)
        {
            return;
        }

        var maximumAllowed = Math.Min(
            CampaignTokenLimits.MaximumGmOutputTokens,
            SelectedGmRoute.MaxOutputTokens);
        if (!int.TryParse(GmMaxOutputTokensText, out var requestedTokens)
            || requestedTokens < 512
            || requestedTokens > maximumAllowed)
        {
            StatusText = LanguageRuntime.Format(
                "Campaigns.Gm.OutputRangeFormat",
                maximumAllowed);
            return;
        }

        await RunUiAsync(async () =>
        {
            await _campaigns.UpdateGmRouteAsync(
                _game.Campaign.Id,
                SelectedGmRoute.ToRoute(
                    0.7,
                    maximumOutputTokens: requestedTokens));
            await LoadGameAsync(_game.Campaign.Id);
            StatusText = LanguageRuntime.Format(
                "Campaigns.Gm.ModelChangedFormat",
                SelectedGmRoute.DisplayLabel,
                requestedTokens);
        });
    }

    private async Task OpenGlobalPromptAsync(object? parameter)
    {
        if (OpenPromptSettings is null
            || parameter is not string keyText
            || !Enum.TryParse<GlobalPromptKey>(keyText, out var key))
        {
            StatusText = LanguageRuntime.GetString("Campaigns.GlobalPrompt.Unavailable");
            return;
        }

        await OpenPromptSettings(key);
    }

    public async Task<bool> ConfirmCanLeaveAsync()
    {
        await ScenarioEditor.FlushDraftAsync();
        if (!IsLobby)
        {
            return true;
        }

        var decision = _interaction.ConfirmUnsavedCampaignLobby(
            string.IsNullOrWhiteSpace(Title)
                ? LanguageRuntime.GetString("Campaigns.Game.Unnamed")
                : Title.Trim());
        switch (decision)
        {
            case UnsavedChangesDecision.Cancel:
                return false;
            case UnsavedChangesDecision.Save:
                await SaveLobbyCoreAsync();
                break;
            case UnsavedChangesDecision.Discard:
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        _draftCampaign = null;
        ShowScreen("library");
        return true;
    }

    private bool CanMoveGmCandidate(int offset)
    {
        if (!IsGmCandidatePending || IsCampaignOperationBusy)
        {
            return false;
        }

        var candidates = GetGmCandidates();
        var targetIndex = SelectedGmCandidateIndex(candidates) + offset;
        return targetIndex >= 0 && targetIndex < candidates.Count;
    }

    private async Task MoveGmCandidateAsync(int offset)
    {
        if (!CanMoveGmCandidate(offset))
        {
            return;
        }

        var candidates = GetGmCandidates();
        var targetIndex = SelectedGmCandidateIndex(candidates) + offset;
        await RunUiAsync(async () =>
        {
            _selectedGmCandidateId = candidates[targetIndex].Id;
            await LoadGameAsync(_game!.Campaign.Id);
            StatusText = LanguageRuntime.Format(
                "Campaigns.Resolve.CandidateChangedFormat",
                targetIndex + 1,
                candidates.Count);
        });
    }

    private async Task LoadGameAsync(string campaignId)
    {
        // Generation progress is application-scoped and may outlive a page load.
        // Drop only other campaigns; preserve live operations for this campaign.
        foreach (var stale in _generationProgresses
                     .Where(pair => pair.Value.CampaignId != campaignId)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _generationProgresses.Remove(stale);
        }
        var previousCampaignId = _game?.Campaign.Id;
        _game = await _campaigns.GetAsync(campaignId)
                ?? throw new InvalidOperationException(
                    LanguageRuntime.GetString("Campaigns.Game.Missing"));
        SettingsPanel.SwitchGame(previousCampaignId, campaignId);
        var flowSnapshot = _flowEngine.Inspect(_game);
        var currentGmCandidateIds = flowSnapshot.ResolutionPlan.CandidateResolutionIds
            .ToHashSet(StringComparer.Ordinal);
        // The flow engine, not UI chronology, owns the set of candidates that
        // may still resolve the current slot.
        var gmCandidates = _game.Events
            .Where(item => currentGmCandidateIds.Contains(item.Id))
            .OrderBy(item => item.SequenceNo)
            .ToArray();
        if (_game.Campaign.Phase != CampaignPhase.ReadyForResolution
            || _game.Campaign.GmKind != CampaignGmKind.Ai
            || gmCandidates.Length <= 1)
        {
            _selectedGmCandidateId = null;
        }
        else if (gmCandidates.All(item => item.Id != _selectedGmCandidateId))
        {
            _selectedGmCandidateId = gmCandidates
                .LastOrDefault(item =>
                    item.GenerationStatus == CampaignGenerationStatus.Completed
                    && item.EndReason == CampaignEndReason.Normal)
                ?.Id
                ?? gmCandidates[^1].Id;
        }
        _gameUiState = CampaignGameUiState.Create(
            _game,
            flowSnapshot);
        await SettingsPanel.RefreshStatusAsync();
        SelectedGm = GmChoices.Single(item => item.Value == _game.Campaign.GmKind);
        SelectedGmRoute = FindRoute(
            _game.Campaign.GmProviderId,
            _game.Campaign.GmModelId);
        GmMaxOutputTokensText = _game.Campaign.GmMaxOutputTokens.ToString();
        Seats.Clear();
        foreach (var participant in _game.Participants
                     .Where(item => item.IsEnabled)
                     .OrderBy(item => item.SortIndex))
        {
            var seat = new CampaignSeatViewModel(participant)
            {
                SelectedRoute = FindRoute(participant.ProviderId, participant.ModelId),
                RoundStatus = RoundStatus(_game, participant, flowSnapshot)
            };
            var actionState = CampaignSeatActionState.Create(
                _game,
                participant,
                flowSnapshot,
                _flowEngine.PlanAction(_game, participant.Id));
            seat.ShowActionButton = actionState.ShowButton;
            seat.CanGenerateAction =
                actionState.CanAct && !IsCampaignOperationBusy;
            seat.IsRetryAction = actionState.IsRetry;
            seat.RetryEventId = actionState.RetryEventId;
            seat.ActionHelpText = actionState.HelpText;
            seat.IsCurrentTurn = flowSnapshot.CurrentParticipantId is not null
                                 && string.Equals(
                                     flowSnapshot.CurrentParticipantId,
                                     participant.Id,
                                     StringComparison.Ordinal);
            Seats.Add(seat);
        }

        Events.Clear();
        var names = _game.Participants.ToDictionary(
            item => item.Id,
            item => item.DisplayName,
            StringComparer.Ordinal);
        var userSeat = _game.Participants.FirstOrDefault(item =>
            item.Kind == CampaignParticipantKind.User);
        var latestPlayerAttempts = _game.Events
            .Where(item =>
                item.RoundNo == _game.Campaign.CurrentRound
                && item.Kind == CampaignEventKind.PlayerIntent)
            .GroupBy(item => item.ActorId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(item => item.SequenceNo)
                    .Last()
                    .Id,
                StringComparer.Ordinal);
        var gmCandidateIds = IsGmCandidatePending
            ? gmCandidates
                .Select(item => item.Id)
                .ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var currentGmResolutionIds = gmCandidates
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        // Keep locked resolutions as canonical history, show current retry
        // candidates as one selectable group, and hide superseded attempts.
        var canonicalGmResolutionIds = _game.Events
            .Where(item =>
                item.Kind == CampaignEventKind.GmResolution
                && item.GenerationStatus == CampaignGenerationStatus.Completed
                && item.IsLocked)
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        var replacedEventIds = BuildReplacedAncestorIds(_game.Events);
        var addedGmCandidateGroup = false;
        foreach (var campaignEvent in _game.Events.OrderBy(item => item.SequenceNo))
        {
            if (gmCandidateIds.Contains(campaignEvent.Id))
            {
                if (addedGmCandidateGroup)
                {
                    continue;
                }

                addedGmCandidateGroup = true;
                var selectedCandidate = gmCandidates.FirstOrDefault(item =>
                                            item.Id == _selectedGmCandidateId)
                                        ?? gmCandidates[^1];
                Events.Add(new CampaignEventItemViewModel(
                    selectedCandidate,
                    "GM",
                    EventKindName(selectedCandidate.Kind),
                    GenerationStatusName(selectedCandidate),
                    selectedCandidate.GenerationStatus
                        == CampaignGenerationStatus.Completed
                        ? selectedCandidate.Content
                        : LanguageRuntime.GetString("Campaigns.Event.InvalidGmCandidate"),
                    CanRetry: false,
                    gmCandidates,
                    SelectedGmCandidateIndex(gmCandidates)));
                continue;
            }

            if (campaignEvent.Kind == CampaignEventKind.GmResolution
                && !canonicalGmResolutionIds.Contains(campaignEvent.Id)
                && !currentGmResolutionIds.Contains(campaignEvent.Id))
            {
                continue;
            }

            if (replacedEventIds.Contains(campaignEvent.Id))
            {
                continue;
            }

            var canRetry = campaignEvent.Kind == CampaignEventKind.PlayerIntent
                           && campaignEvent.GenerationStatus is (
                               CampaignGenerationStatus.Failed
                               or CampaignGenerationStatus.Interrupted)
                           && _game.Campaign.Phase
                           == CampaignPhase.AwaitingActions
                           && latestPlayerAttempts.GetValueOrDefault(
                               campaignEvent.ActorId) == campaignEvent.Id
                           && _game.Participants.Any(item =>
                               item.Id == campaignEvent.ActorId
                               && item.Kind == CampaignParticipantKind.Ai);
            if (!CanDisplayEvent(_game, campaignEvent, userSeat) && !canRetry)
            {
                continue;
            }

            var content = CanDisplayEvent(_game, campaignEvent, userSeat)
                ? campaignEvent.Content
                : LanguageRuntime.GetString("Campaigns.Event.SecretActionFailed");
            Events.Add(new CampaignEventItemViewModel(
                campaignEvent,
                names.GetValueOrDefault(campaignEvent.ActorId, campaignEvent.ActorId),
                EventKindName(campaignEvent.Kind),
                GenerationStatusName(campaignEvent),
                content,
                canRetry));
        }

        await ContextPreview.RefreshAsync(_game);
        ShowScreen("game");
        RaiseGameProperties();
    }

    private static HashSet<string> BuildReplacedAncestorIds(
        IReadOnlyList<CampaignEvent> events)
    {
        var eventsById = events.ToDictionary(
            item => item.Id,
            StringComparer.Ordinal);
        var replacedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var successfulEvent in events.Where(item =>
                     item.GenerationStatus == CampaignGenerationStatus.Completed
                     && item.IsLocked))
        {
            // Follow the full retry chain so an accepted third attempt hides both
            // earlier attempts. The visited set also bounds malformed cyclic legacy data.
            var ancestorId = successfulEvent.ReplacesEventId;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (!string.IsNullOrWhiteSpace(ancestorId)
                   && visited.Add(ancestorId)
                   && eventsById.TryGetValue(ancestorId, out var ancestor))
            {
                replacedIds.Add(ancestor.Id);
                ancestorId = ancestor.ReplacesEventId;
            }
        }

        return replacedIds;
    }

    private bool CanDisplayEvent(
        CampaignAggregate aggregate,
        CampaignEvent campaignEvent,
        CampaignParticipant? userSeat)
    {
        if (aggregate.Campaign.GmKind == CampaignGmKind.User)
        {
            return true;
        }

        return userSeat is null
            ? _flowEngine.IsEventVisibleToObserver(
                aggregate,
                campaignEvent)
            : _flowEngine.IsEventVisibleToParticipant(
                aggregate,
                campaignEvent,
                userSeat);
    }

    private void ApplyScenarioToLobby(CampaignScenario scenario)
    {
        Title = scenario.Title;
        WorldSetting = scenario.WorldSetting;
        Rules = scenario.PublicRules;
        OpeningPrompt = scenario.OpeningSetup;
        SelectedFlow = FlowChoices[0];
        SelectedGm = GmChoices[0];
        UserAlsoPlayer = true;
        PlayerHistoryBudget = 12000;
        GmHistoryBudget = 20000;
    }

    private async Task<string> NextCampaignTitleAsync(
        CampaignScenario scenario)
    {
        var baseTitle = scenario.Title.Trim();
        var prefix = $"{baseTitle}-";
        var sameStory = (await _campaigns.ListAsync())
            .Where(item => string.Equals(
                item.StoryId,
                scenario.Id,
                StringComparison.Ordinal))
            .ToArray();
        var usedTitles = sameStory
            .Select(item => item.Title)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var highestSuffix = sameStory
            .Select(item => item.Title)
            .Where(item => item.StartsWith(
                prefix,
                StringComparison.OrdinalIgnoreCase))
            .Select(item => int.TryParse(
                item[prefix.Length..],
                out var suffix)
                ? suffix
                : 0)
            .DefaultIfEmpty(0)
            .Max();
        var next = Math.Max(sameStory.Length + 1, highestSuffix + 1);
        while (usedTitles.Contains($"{prefix}{next}"))
        {
            next++;
        }

        return $"{prefix}{next}";
    }

    private void ResetCharacterChoices()
    {
        foreach (var choice in CharacterChoices)
        {
            choice.IsSelected = false;
            choice.IncludeMemory = false;
        }
        RefreshCharacterSelectionState();
    }

    private void OnCharacterChoicePropertyChanged(
        object? sender,
        PropertyChangedEventArgs eventArgs)
    {
        if (_updatingCharacterSelection
            || eventArgs.PropertyName
                != nameof(CampaignCharacterChoiceViewModel.IsSelected)
            || sender is not CampaignCharacterChoiceViewModel changed)
        {
            return;
        }

        if (SelectedAiPlayerCount > 4)
        {
            _updatingCharacterSelection = true;
            changed.IsSelected = false;
            _updatingCharacterSelection = false;
            StatusText = LanguageRuntime.GetString("Campaigns.Lobby.MaxAiStatus");
        }

        RefreshCharacterSelectionState();
    }

    private void RefreshCharacterSelectionState()
    {
        var selectedCount = SelectedAiPlayerCount;
        foreach (var choice in CharacterChoices)
        {
            choice.IsSelectionEnabled =
                choice.IsSelected || selectedCount < 4;
        }

        OnPropertyChanged(nameof(SelectedAiPlayerCount));
        OnPropertyChanged(nameof(LobbyRosterText));
    }

    private CampaignModelOption? FindRoute(string? providerId, string? modelId) =>
        ModelOptions.FirstOrDefault(item =>
            item.ProviderId == providerId && item.ModelId == modelId);

    private static void ApplyRoute(Campaign campaign, CampaignModelOption route)
    {
        campaign.GmProviderId = route.ProviderId;
        campaign.GmModelId = route.ModelId;
        campaign.GmContextLimit = route.ContextLimit;
        campaign.GmMaxOutputTokens = CampaignTokenLimits.ClampGmOutputTokens(
            route.MaxOutputTokens);
        campaign.GmTemperature = 0.7;
        campaign.GmTopP = 1;
    }

    private void ShowScreen(string screen)
    {
        _screen = screen;
        OnPropertyChanged(nameof(IsLibrary));
        OnPropertyChanged(nameof(IsScenarioEditor));
        OnPropertyChanged(nameof(IsLobby));
        OnPropertyChanged(nameof(IsGame));
    }

    private void RefreshSeatActionStates()
    {
        if (_game is null)
        {
            return;
        }

        foreach (var seat in Seats)
        {
            var actionState = CampaignSeatActionState.Create(
                _game,
                seat.Participant,
                _flowEngine.Inspect(_game),
                _flowEngine.PlanAction(_game, seat.Id));
            var contextBlocked = ContextPreview.TryGetSeatBlockReason(
                seat.Id,
                out var contextReason);
            seat.ShowActionButton = actionState.ShowButton;
            seat.CanGenerateAction =
                actionState.CanAct
                && !IsCampaignOperationBusy
                && !contextBlocked;
            seat.IsRetryAction = actionState.IsRetry;
            seat.RetryEventId = actionState.RetryEventId;
            seat.ActionHelpText = contextBlocked
                ? LanguageRuntime.Format("Campaigns.Seat.ContextInsufficientFormat", contextReason)
                : actionState.HelpText;
        }
    }

    private void RaiseGameProperties()
    {
        OnPropertyChanged(nameof(GameTitle));
        OnPropertyChanged(nameof(GamePhaseText));
        OnPropertyChanged(nameof(SaveStateText));
        OnPropertyChanged(nameof(CanSubmitUserAction));
        OnPropertyChanged(nameof(CanResolve));
        OnPropertyChanged(nameof(HasUserSeat));
        OnPropertyChanged(nameof(HasPendingUserJoin));
        OnPropertyChanged(nameof(ShowUserJoinSection));
        OnPropertyChanged(nameof(CanScheduleUserJoin));
        OnPropertyChanged(nameof(ShowUserActionSection));
        OnPropertyChanged(nameof(ShowBlindAiAction));
        OnPropertyChanged(nameof(CanGenerateBlindAiActions));
        OnPropertyChanged(nameof(ShowResolveSection));
        OnPropertyChanged(nameof(CurrentStepTitle));
        OnPropertyChanged(nameof(CurrentStepDescription));
        OnPropertyChanged(nameof(CurrentStepProgressText));
        OnPropertyChanged(nameof(GmModeText));
        OnPropertyChanged(nameof(ParticipationModeText));
        OnPropertyChanged(nameof(UserActionHelpText));
        OnPropertyChanged(nameof(BlindAiActionHelpText));
        OnPropertyChanged(nameof(ResolveButtonText));
        OnPropertyChanged(nameof(ResolveHelpText));
        OnPropertyChanged(nameof(HasGmCandidateNavigation));
        OnPropertyChanged(nameof(GmCandidateNavigationLabel));
        OnPropertyChanged(nameof(IsSelectedGmCandidateValid));
        OnPropertyChanged(nameof(IsCampaignOperationBusy));
        OnPropertyChanged(nameof(IsRequestReceiving));
        OnPropertyChanged(nameof(RequestProgressText));
        OnPropertyChanged(nameof(RequestReceivedTokenText));
        OnPropertyChanged(nameof(ScheduleUserJoinButtonText));
        OnPropertyChanged(nameof(ScheduleUserJoinHelpText));
        SettingsPanel.NotifyGameStateChanged();
        PreviousGmCandidateCommand.RaiseCanExecuteChanged();
        NextGmCandidateCommand.RaiseCanExecuteChanged();
    }

    private async Task RunUiAsync(Func<Task> operation)
    {
        if (IsCampaignOperationBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await operation();
        }
        catch (Exception exception)
        {
            StatusText = LanguageRuntime.ErrorMessage(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnCampaignGenerationProgressChanged(
        object? sender,
        CampaignGenerationProgress progress)
    {
        if (_disposed || _game is null || progress.CampaignId != _game.Campaign.Id)
        {
            return;
        }

        RunOnUi(() =>
        {
            if (_disposed || progress.CampaignId != _game?.Campaign.Id) return;
            if (progress.Status is CampaignGenerationStatus.Queued
                or CampaignGenerationStatus.Streaming)
            {
                _generationProgresses[progress.EventId] = progress;
            }
            else
            {
                _generationProgresses.Remove(progress.EventId);
            }
            OnPropertyChanged(nameof(IsRequestReceiving));
            OnPropertyChanged(nameof(RequestProgressText));
            OnPropertyChanged(nameof(RequestReceivedTokenText));
        });
    }

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

    private static string RoundStatus(
        CampaignAggregate aggregate,
        CampaignParticipant participant,
        CampaignFlowSnapshot snapshot)
    {
        var latest = aggregate.Events.LastOrDefault(item =>
            item.RoundNo == aggregate.Campaign.CurrentRound
            && item.Kind == CampaignEventKind.PlayerIntent
            && item.ActorId == participant.Id);
        if (latest is not null)
        {
            return GenerationStatusName(latest);
        }

        if (snapshot.CurrentParticipantId is not null)
        {
            return string.Equals(
                       snapshot.CurrentParticipantId,
                       participant.Id,
                       StringComparison.Ordinal)
                ? LanguageRuntime.GetString("Campaigns.Seat.Current")
                : LanguageRuntime.GetString("Campaigns.Seat.WaitingRound");
        }

        return LanguageRuntime.GetString("Campaigns.Generation.Waiting");
    }

    private static string GenerationStatusName(CampaignEvent campaignEvent) =>
        campaignEvent.GenerationStatus switch
        {
            CampaignGenerationStatus.Queued => LanguageRuntime.GetString("Campaigns.Generation.Queued"),
            CampaignGenerationStatus.Streaming => LanguageRuntime.GetString("Campaigns.Generation.Streaming"),
            CampaignGenerationStatus.Completed => campaignEvent.IsLocked
                ? LanguageRuntime.GetString("Campaigns.Generation.Locked")
                : LanguageRuntime.GetString("Campaigns.Generation.CompletedPending"),
            CampaignGenerationStatus.Interrupted => LanguageRuntime.GetString("Campaigns.Generation.Interrupted"),
            CampaignGenerationStatus.Failed =>
                LanguageRuntime.Format(
                    "Campaigns.Generation.FailedFormat",
                    EndReasonName(campaignEvent.EndReason)),
            _ => LanguageRuntime.GetString("Campaigns.Generation.LocalEvent")
        };

    private static string EndReasonName(CampaignEndReason reason) => reason switch
    {
        CampaignEndReason.OutputLimit => LanguageRuntime.GetString("Campaigns.End.OutputLimit"),
        CampaignEndReason.ContextLimit => LanguageRuntime.GetString("Campaigns.End.ContextLimit"),
        CampaignEndReason.RepetitionDetected => LanguageRuntime.GetString("Campaigns.End.Repetition"),
        CampaignEndReason.StreamDisconnected => LanguageRuntime.GetString("Campaigns.End.Disconnected"),
        CampaignEndReason.GlobalStop => LanguageRuntime.GetString("Campaigns.End.GlobalStop"),
        CampaignEndReason.UserStopped => LanguageRuntime.GetString("Campaigns.End.UserStopped"),
        CampaignEndReason.ProtocolViolation => LanguageRuntime.GetString("Campaigns.End.Protocol"),
        CampaignEndReason.NarrativeAuthorityViolation => LanguageRuntime.GetString("Campaigns.End.Authority"),
        _ => LanguageRuntime.GetString("Campaigns.End.Provider")
    };

    private static string EventKindName(CampaignEventKind kind) => kind switch
    {
        CampaignEventKind.GmOpening => LanguageRuntime.GetString("Campaigns.Event.GmOpening"),
        CampaignEventKind.PlayerIntent => LanguageRuntime.GetString("Campaigns.Event.PlayerIntent"),
        CampaignEventKind.GmResolution => LanguageRuntime.GetString("Campaigns.Event.GmResolution"),
        CampaignEventKind.DiceRoll => LanguageRuntime.GetString("Campaigns.Event.Dice"),
        CampaignEventKind.System => LanguageRuntime.GetString("Campaigns.Event.System"),
        CampaignEventKind.StateDelta => LanguageRuntime.GetString("Campaigns.Event.StateDelta"),
        _ => LanguageRuntime.GetString("Campaigns.Event.Private")
    };

    private static string FlowName(CampaignFlowPreset flow) => flow switch
    {
        CampaignFlowPreset.CollaborativeTable => LanguageRuntime.GetString("Campaigns.Flow.Collaborative"),
        CampaignFlowPreset.BlindSubmission => LanguageRuntime.GetString("Campaigns.Flow.Blind"),
        _ => LanguageRuntime.GetString("Campaigns.Flow.Strict")
    };

    private static string PhaseName(CampaignPhase phase) => phase switch
    {
        CampaignPhase.AwaitingActions => LanguageRuntime.GetString("Campaigns.Phase.Awaiting"),
        CampaignPhase.ReadyForResolution => LanguageRuntime.GetString("Campaigns.Phase.Ready"),
        CampaignPhase.Resolving => LanguageRuntime.GetString("Campaigns.Phase.Resolving"),
        CampaignPhase.Paused => LanguageRuntime.GetString("Campaigns.Phase.Paused"),
        CampaignPhase.Completed => LanguageRuntime.GetString("Campaigns.Phase.Completed"),
        _ => phase.ToString()
    };
}
