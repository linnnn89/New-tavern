using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using TavernDesk.App.Presentation;
using TavernDesk.App.Services;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;
using TavernDesk.Infrastructure.Providers;

namespace TavernDesk.App.ViewModels;

public sealed class CampaignsViewModel : ViewModelBase
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
    private string _statusText = "选择剧本创建新局，或继续已有跑团。";
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
    private string _diceExpression = "1d20";
    private bool _isBusy;
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
        IFileDialogService fileDialog)
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

        FlowChoices =
        [
            new CampaignFlowChoice(
                CampaignFlowPreset.CollaborativeTable,
                "协作圆桌",
                "按席位依次行动，后行动者能看到同伴的公开行动，适合共同冒险。"),
            new CampaignFlowChoice(
                CampaignFlowPreset.BlindSubmission,
                "秘密同投",
                "AI 并发提交且彼此看不到当轮意图，只有 GM 收齐后裁定，适合竞争和秘密身份。"),
            new CampaignFlowChoice(
                CampaignFlowPreset.StrictInitiative,
                "严格先攻",
                "每次只轮到一个席位，GM 随后立即裁定，再进入下一席位。")
        ];
        GmChoices =
        [
            new CampaignGmChoice(
                CampaignGmKind.Ai,
                "AI 担任 GM",
                "由独立模型读取 GM 专用说明并裁定。"),
            new CampaignGmChoice(
                CampaignGmKind.User,
                "USER 担任 GM",
                "你手动主持；若同时勾选 USER 玩家，即“裁判下场踢球了”模式。")
        ];
        UserParticipationChoices =
        [
            new CampaignUserParticipationChoice(
                true,
                "我要作为玩家参与",
                "创建并启用一个 USER 玩家席位；你可以在每回合提交自己的行动。"),
            new CampaignUserParticipationChoice(
                false,
                "仅观看 AI 演出",
                "本局只有 AI 玩家行动；游玩页不会显示无法使用的 USER 输入框。")
        ];
        _selectedFlow = FlowChoices[0];
        _selectedGm = GmChoices[0];
        _selectedUserParticipation = UserParticipationChoices[0];

        ImportScenarioCommand = new AsyncRelayCommand(ImportScenarioAsync);
        OpenScenarioLobbyCommand = new AsyncRelayCommand(OpenScenarioLobbyAsync);
        ContinueCampaignCommand = new AsyncRelayCommand(ContinueSelectedCampaignAsync);
        CloneCampaignCommand = new AsyncRelayCommand(CloneSelectedCampaignAsync);
        BackToLibraryCommand = new AsyncRelayCommand(BackToLibraryAsync);
        SaveLobbyCommand = new AsyncRelayCommand(SaveLobbyAsync);
        StartCampaignCommand = new AsyncRelayCommand(StartCampaignAsync);
        RefreshGameCommand = new AsyncRelayCommand(RefreshGameAsync);
        SubmitUserActionCommand = new AsyncRelayCommand(SubmitUserActionAsync);
        GenerateAiActionsCommand = new AsyncRelayCommand(GenerateAiActionsAsync);
        GenerateAiSeatActionCommand = new AsyncRelayCommand(
            GenerateAiSeatActionAsync);
        ResolveRoundCommand = new AsyncRelayCommand(ResolveRoundAsync);
        ScheduleUserJoinCommand = new AsyncRelayCommand(
            ScheduleUserJoinAsync);
        RollDiceCommand = new AsyncRelayCommand(RollDiceAsync);
        RetryEventCommand = new AsyncRelayCommand(RetryEventAsync);
        ApplySeatRouteCommand = new AsyncRelayCommand(ApplySeatRouteAsync);
        ApplyGmRouteCommand = new AsyncRelayCommand(ApplyGmRouteAsync);
        OpenGlobalPromptCommand = new AsyncRelayCommand(OpenGlobalPromptAsync);
    }

    public ObservableCollection<CampaignScenario> Scenarios { get; } = [];
    public ObservableCollection<CampaignSummaryItemViewModel> Campaigns { get; } = [];
    public ObservableCollection<CampaignCharacterChoiceViewModel> CharacterChoices { get; } = [];
    public ObservableCollection<CampaignModelOption> ModelOptions { get; } = [];
    public ObservableCollection<CampaignSeatViewModel> Seats { get; } = [];
    public ObservableCollection<CampaignEventItemViewModel> Events { get; } = [];
    public IReadOnlyList<CampaignFlowChoice> FlowChoices { get; }
    public IReadOnlyList<CampaignGmChoice> GmChoices { get; }
    public IReadOnlyList<CampaignUserParticipationChoice>
        UserParticipationChoices { get; }

    public AsyncRelayCommand ImportScenarioCommand { get; }
    public AsyncRelayCommand OpenScenarioLobbyCommand { get; }
    public AsyncRelayCommand ContinueCampaignCommand { get; }
    public AsyncRelayCommand CloneCampaignCommand { get; }
    public AsyncRelayCommand BackToLibraryCommand { get; }
    public AsyncRelayCommand SaveLobbyCommand { get; }
    public AsyncRelayCommand StartCampaignCommand { get; }
    public AsyncRelayCommand RefreshGameCommand { get; }
    public AsyncRelayCommand SubmitUserActionCommand { get; }
    public AsyncRelayCommand GenerateAiActionsCommand { get; }
    public AsyncRelayCommand GenerateAiSeatActionCommand { get; }
    public AsyncRelayCommand ResolveRoundCommand { get; }
    public AsyncRelayCommand ScheduleUserJoinCommand { get; }
    public AsyncRelayCommand RollDiceCommand { get; }
    public AsyncRelayCommand RetryEventCommand { get; }
    public AsyncRelayCommand ApplySeatRouteCommand { get; }
    public AsyncRelayCommand ApplyGmRouteCommand { get; }
    public AsyncRelayCommand OpenGlobalPromptCommand { get; }
    public Func<GlobalPromptKey, Task>? OpenPromptSettings { get; set; }

    public bool IsLibrary => _screen == "library";
    public bool IsLobby => _screen == "lobby";
    public bool IsGame => _screen == "game";
    public bool IsAiGm => SelectedGm.Value == CampaignGmKind.Ai;
    public bool IsUserGm => SelectedGm.Value == CampaignGmKind.User;
    public int SelectedAiPlayerCount =>
        CharacterChoices.Count(item => item.IsSelected);
    public string LobbyRosterText =>
        $"当前阵容：1 GM + {(UserAlsoPlayer ? 1 : 0)} USER 玩家"
        + $" + {SelectedAiPlayerCount} AI 玩家";
    public bool CanSubmitUserAction =>
        !IsBusy
        && _gameUiState.UserSeatCanAct
        && !string.IsNullOrWhiteSpace(ActionInput);
    public bool CanResolve =>
        !IsBusy
        && _gameUiState.ShowResolveSection
        && (_game?.Campaign.GmKind == CampaignGmKind.Ai
            || !string.IsNullOrWhiteSpace(GmResolutionInput));
    public bool HasUserSeat => _gameUiState.HasUserSeat;
    public bool HasPendingUserJoin => _gameUiState.HasPendingUserJoin;
    public bool ShowUserJoinSection => !HasUserSeat;
    public bool CanScheduleUserJoin =>
        !IsBusy && _gameUiState.CanScheduleUserJoin;
    public bool ShowUserActionSection =>
        _gameUiState.ShowUserActionSection;
    public bool ShowBlindAiAction =>
        _gameUiState.ShowBlindAiAction;
    public bool CanGenerateBlindAiActions =>
        !IsBusy && _gameUiState.CanGenerateBlindAiActions;
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
            ? "先输入你的本回合行动，按钮才会启用。"
            : _gameUiState.UserActionHelpText;
    public string BlindAiActionHelpText =>
        _gameUiState.BlindAiActionHelpText;
    public string ResolveButtonText =>
        _game?.Campaign.GmKind == CampaignGmKind.Ai
            ? AiGmResolutionNeedsRetry
                ? "重试 AI GM 裁定"
                : "让 AI GM 裁定本回合"
            : "提交我的 GM 裁定";
    public string ResolveHelpText =>
        _game?.Campaign.GmKind == CampaignGmKind.User
        && _gameUiState.ShowResolveSection
        && string.IsNullOrWhiteSpace(GmResolutionInput)
            ? "先填写本回合的 GM 裁定，按钮才会启用。"
            : _gameUiState.ResolveHelpText;
    public string ScheduleUserJoinButtonText =>
        HasPendingUserJoin
            ? "USER 将从下一回合加入"
            : "从下一回合加入本局";
    public string ScheduleUserJoinHelpText =>
        HasPendingUserJoin
            ? "加入已安排；当前回合阵容保持不变，完成 GM 裁定后生效。"
            : "为当前跑团预约一个 USER 玩家席位；从下一完整回合起生效，加入后不能移除。";
    public string GameTitle => _game?.Campaign.Title ?? string.Empty;
    public string GamePhaseText => _game is null
        ? string.Empty
        : $"第 {_game.Campaign.CurrentRound} 回合 · {FlowName(_game.Campaign.FlowPreset)} · {PhaseName(_game.Campaign.Phase)}";
    public string SaveStateText => _game is null
        ? string.Empty
        : $"已自动保存到本地 · 状态版本 {_game.Campaign.StateVersion}";

    private bool AiGmResolutionNeedsRetry =>
        _game?.Campaign.GmKind == CampaignGmKind.Ai
        && _game.Events
            .Where(item =>
                item.RoundNo == _game.Campaign.CurrentRound
                && item.Kind == CampaignEventKind.GmResolution)
            .OrderBy(item => item.SequenceNo)
            .LastOrDefault()
            ?.GenerationStatus is (
                CampaignGenerationStatus.Failed
                or CampaignGenerationStatus.Interrupted);

    public CampaignScenario? SelectedScenario
    {
        get => _selectedScenario;
        set => SetProperty(ref _selectedScenario, value);
    }

    public CampaignSummaryItemViewModel? SelectedCampaign
    {
        get => _selectedCampaign;
        set => SetProperty(ref _selectedCampaign, value);
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
                RefreshSeatActionStates();
                RaiseGameProperties();
            }
        }
    }

    public async Task LoadAsync()
    {
        await RunUiAsync(async () =>
        {
            await LoadReferenceDataAsync();
            await RefreshLibraryAsync();
            if (_game is not null)
            {
                await LoadGameAsync(_game.Campaign.Id);
            }
        });
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
            var models = await _models.ListAsync(provider.Id);
            if (provider.AdapterKind == ProviderAdapterKind.GrokCli
                && models.Count == 0)
            {
                ModelOptions.Add(new CampaignModelOption(
                    provider.Id,
                    provider.Name,
                    GrokCliProviderGateway.DefaultModelId,
                    "订阅默认模型",
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

        SelectedScenario ??= Scenarios.FirstOrDefault();
        SelectedCampaign = Campaigns.FirstOrDefault();
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
                ? $"已导入剧本“{result.Scenario.Title}”。"
                : $"已导入剧本“{result.Scenario.Title}”；{string.Join("；", result.Warnings)}";
        });
    }

    private async Task OpenScenarioLobbyAsync()
    {
        if (SelectedScenario is null)
        {
            StatusText = "请先选择一个剧本。";
            return;
        }

        await RunUiAsync(async () =>
        {
            _draftCampaign = null;
            ResetCharacterChoices();
            ApplyScenarioToLobby(SelectedScenario);
            await Task.CompletedTask;
            ShowScreen("lobby");
            StatusText = "开局前可修改世界观、规则、角色记忆导入和每个席位的模型；开始后这些内容冻结。";
        });
    }

    private async Task ContinueSelectedCampaignAsync()
    {
        if (SelectedCampaign is null)
        {
            StatusText = "请先选择一局跑团。";
            return;
        }

        await RunUiAsync(async () =>
        {
            var aggregate = await _campaigns.GetAsync(SelectedCampaign.Id)
                            ?? throw new InvalidOperationException("跑团不存在。");
            if (aggregate.Campaign.Status == CampaignStatus.Draft)
            {
                await LoadDraftIntoLobbyAsync(aggregate);
                StatusText =
                    "大厅草稿已载入；确认参与方式、角色和模型后再开始本局。";
            }
            else
            {
                await LoadGameAsync(aggregate.Campaign.Id);
                StatusText =
                    "本局已从本地记录载入；请按右侧“当前步骤”继续。";
            }
        });
    }

    private async Task CloneSelectedCampaignAsync()
    {
        if (SelectedCampaign is null)
        {
            StatusText = "请先选择一局跑团。";
            return;
        }

        await RunUiAsync(async () =>
        {
            var clone = await _campaigns.CloneAsDraftAsync(SelectedCampaign.Id);
            await RefreshLibraryAsync();
            await LoadDraftIntoLobbyAsync(clone);
            StatusText = "已基于同一故事创建独立新局；旧局记录不会进入新局。";
        });
    }

    private async Task BackToLibraryAsync()
    {
        await RunUiAsync(async () =>
        {
            await RefreshLibraryAsync();
            ShowScreen("library");
            StatusText = "所有已开始的跑团均已即时保存，可随时继续或另开一局。";
        });
    }

    private async Task SaveLobbyAsync()
    {
        await RunUiAsync(async () =>
        {
            await SaveLobbyCoreAsync();
            await RefreshLibraryAsync();
            StatusText = "起始大厅草稿已保存；尚未冻结。";
        });
    }

    private async Task StartCampaignAsync()
    {
        await RunUiAsync(async () =>
        {
            var campaign = await SaveLobbyCoreAsync();
            var started = await _runner.StartAsync(campaign.Id);
            await LoadGameAsync(started.Campaign.Id);
            StatusText = "游戏已开始：剧本、角色快照、记忆导入和初始配置已经冻结。";
        });
    }

    private async Task<Campaign> SaveLobbyCoreAsync()
    {
        if (SelectedScenario is null)
        {
            throw new InvalidOperationException("起始大厅没有关联剧本。");
        }

        var selectedCharacters = CharacterChoices
            .Where(item => item.IsSelected)
            .ToArray();
        if (selectedCharacters.Length > 4)
        {
            throw new InvalidOperationException("一局最多选择 4 个 AI 角色。");
        }

        if (!UserAlsoPlayer && selectedCharacters.Length == 0)
        {
            throw new InvalidOperationException(
                "至少需要一个玩家：请让 USER 作为玩家，或选择至少一个 AI 角色。");
        }

        if (selectedCharacters.Any(item => item.SelectedRoute is null))
        {
            throw new InvalidOperationException("每个 AI 玩家都必须选择模型。");
        }

        if (SelectedGm.Value == CampaignGmKind.Ai && SelectedGmRoute is null)
        {
            throw new InvalidOperationException("AI GM 必须选择模型。");
        }

        var campaign = _draftCampaign ?? new Campaign
        {
            StoryId = SelectedScenario.Id
        };
        campaign.Title = Title.Trim();
        campaign.WorldSetting = WorldSetting.Trim();
        campaign.Rules = Rules.Trim();
        campaign.OpeningPrompt = OpeningPrompt.Trim();
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
                choice.IncludeOriginalWorldKnowledge);
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
            choice.IncludeOriginalWorldKnowledge =
                participant.OriginalWorldKnowledgeSnapshot is not ("" or "{}");
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
            StatusText = "已重新载入本局最新状态；没有调用任何模型。";
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
            StatusText = "USER 行动已写入本回合独立缓存。";
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
                ? $"已收回 {results.Count} 个秘密 AI 行动。"
                : $"已收回 {results.Count} 个缓存，其中 {failures} 个失败或中断，可在记录中重试。";
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
            var result = await _runner.GenerateAiActionAsync(
                _game.Campaign.Id,
                seat.Id);
            await LoadGameAsync(_game.Campaign.Id);
            StatusText = result.GenerationStatus
                         == CampaignGenerationStatus.Completed
                ? $"{seat.Name} 的本回合行动已锁定。"
                : $"{seat.Name} 的行动未完成；请在跑团记录中重试，或先切换该席位模型。";
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
            if (_game.Campaign.GmKind == CampaignGmKind.User)
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
            StatusText = resolution.GenerationStatus
                         == CampaignGenerationStatus.Completed
                ? "GM 裁定已保存，已进入下一行动阶段。"
                : resolution.EndReason == CampaignEndReason.ProtocolViolation
                    ? "本次 AI GM 输出缺少有效的“下一轮评定参考”；原文已保留，回合未推进，请重试。"
                    : $"本次 GM 裁定未完成：{EndReasonName(resolution.EndReason)}。原记录已保留，请重试。";
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
            StatusText =
                "USER 已安排从下一完整回合加入；当前回合仍按原阵容完成。";
        });
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
            StatusText = $"掷骰结果：{roll.Content}";
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
                ? "AI 行动重试成功，新缓存已锁定；旧失败缓存保留用于审计。"
                : "重试仍未完成，可再次重试或更换该席位模型。";
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
            StatusText = $"已把“{seat.Name}”切换到 {seat.SelectedRoute.DisplayLabel}；角色与记忆快照未改变。";
        });
    }

    private async Task ApplyGmRouteAsync()
    {
        if (_game is null || SelectedGmRoute is null)
        {
            return;
        }

        await RunUiAsync(async () =>
        {
            await _campaigns.UpdateGmRouteAsync(
                _game.Campaign.Id,
                SelectedGmRoute.ToRoute(0.7));
            await LoadGameAsync(_game.Campaign.Id);
            StatusText = $"GM 已切换到 {SelectedGmRoute.DisplayLabel}。";
        });
    }

    private async Task OpenGlobalPromptAsync(object? parameter)
    {
        if (OpenPromptSettings is null
            || parameter is not string keyText
            || !Enum.TryParse<GlobalPromptKey>(keyText, out var key))
        {
            StatusText = "当前窗口不能打开全局提示词设置。";
            return;
        }

        await OpenPromptSettings(key);
    }

    private async Task LoadGameAsync(string campaignId)
    {
        _game = await _campaigns.GetAsync(campaignId)
                ?? throw new InvalidOperationException("跑团不存在。");
        _gameUiState = CampaignGameUiState.Create(_game);
        SelectedGm = GmChoices.Single(item => item.Value == _game.Campaign.GmKind);
        SelectedGmRoute = FindRoute(
            _game.Campaign.GmProviderId,
            _game.Campaign.GmModelId);
        Seats.Clear();
        foreach (var participant in _game.Participants
                     .Where(item => item.IsEnabled)
                     .OrderBy(item => item.SortIndex))
        {
            var seat = new CampaignSeatViewModel(participant)
            {
                SelectedRoute = FindRoute(participant.ProviderId, participant.ModelId),
                RoundStatus = RoundStatus(_game, participant)
            };
            var actionState = CampaignSeatActionState.Create(
                _game,
                participant);
            seat.ShowActionButton = actionState.ShowButton;
            seat.CanGenerateAction =
                actionState.CanAct && !IsBusy;
            seat.ActionHelpText = actionState.HelpText;
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
        foreach (var campaignEvent in _game.Events.OrderBy(item => item.SequenceNo))
        {
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
                : "该 AI 的秘密行动生成失败；正文未向玩家公开。";
            Events.Add(new CampaignEventItemViewModel(
                campaignEvent,
                names.GetValueOrDefault(campaignEvent.ActorId, campaignEvent.ActorId),
                EventKindName(campaignEvent.Kind),
                GenerationStatusName(campaignEvent),
                content,
                canRetry));
        }

        ShowScreen("game");
        RaiseGameProperties();
    }

    private static bool CanDisplayEvent(
        CampaignAggregate aggregate,
        CampaignEvent campaignEvent,
        CampaignParticipant? userSeat)
    {
        if (aggregate.Campaign.GmKind == CampaignGmKind.User)
        {
            return true;
        }

        if (aggregate.Campaign.FlowPreset
            == CampaignFlowPreset.BlindSubmission
            && campaignEvent.Kind == CampaignEventKind.PlayerIntent
            && campaignEvent.RoundNo < aggregate.Campaign.CurrentRound
            && campaignEvent.GenerationStatus
            == CampaignGenerationStatus.Completed
            && campaignEvent.IsLocked)
        {
            return true;
        }

        return campaignEvent.Visibility switch
        {
            CampaignVisibility.Public => true,
            CampaignVisibility.Private => userSeat is not null
                                              && campaignEvent.RecipientId
                                              == userSeat.Id,
            _ => false
        };
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

    private void ResetCharacterChoices()
    {
        foreach (var choice in CharacterChoices)
        {
            choice.IsSelected = false;
            choice.IncludeMemory = false;
            choice.IncludeOriginalWorldKnowledge = false;
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
            StatusText = "一局最多只能加入 4 名 AI 玩家。";
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
        campaign.GmMaxOutputTokens = Math.Min(route.MaxOutputTokens, 4096);
        campaign.GmTemperature = 0.7;
        campaign.GmTopP = 1;
    }

    private void ShowScreen(string screen)
    {
        _screen = screen;
        OnPropertyChanged(nameof(IsLibrary));
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
                seat.Participant);
            seat.ShowActionButton = actionState.ShowButton;
            seat.CanGenerateAction =
                actionState.CanAct && !IsBusy;
            seat.ActionHelpText = actionState.HelpText;
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
        OnPropertyChanged(nameof(ScheduleUserJoinButtonText));
        OnPropertyChanged(nameof(ScheduleUserJoinHelpText));
    }

    private async Task RunUiAsync(Func<Task> operation)
    {
        if (IsBusy)
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
            StatusText = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string RoundStatus(
        CampaignAggregate aggregate,
        CampaignParticipant participant)
    {
        var latest = aggregate.Events.LastOrDefault(item =>
            item.RoundNo == aggregate.Campaign.CurrentRound
            && item.Kind == CampaignEventKind.PlayerIntent
            && item.ActorId == participant.Id);
        if (latest is not null)
        {
            return GenerationStatusName(latest);
        }

        if (aggregate.Campaign.FlowPreset == CampaignFlowPreset.StrictInitiative)
        {
            var enabled = aggregate.Participants
                .Where(item => item.IsEnabled)
                .OrderBy(item => item.SortIndex)
                .ToArray();
            return enabled[aggregate.Campaign.CurrentTurnIndex % enabled.Length].Id
                   == participant.Id
                ? "当前行动席位"
                : "等待轮次";
        }

        return "等待行动";
    }

    private static string GenerationStatusName(CampaignEvent campaignEvent) =>
        campaignEvent.GenerationStatus switch
        {
            CampaignGenerationStatus.Queued => "排队中",
            CampaignGenerationStatus.Streaming => "接收中",
            CampaignGenerationStatus.Completed => "已锁定",
            CampaignGenerationStatus.Interrupted => "已中断，可重试",
            CampaignGenerationStatus.Failed =>
                $"失败：{EndReasonName(campaignEvent.EndReason)}",
            _ => "本地事件"
        };

    private static string EndReasonName(CampaignEndReason reason) => reason switch
    {
        CampaignEndReason.OutputLimit => "达到输出上限",
        CampaignEndReason.ContextLimit => "超出上下文",
        CampaignEndReason.RepetitionDetected => "检测到重复死循环",
        CampaignEndReason.StreamDisconnected => "流式连接中断",
        CampaignEndReason.GlobalStop => "全部 API 已停止",
        CampaignEndReason.UserStopped => "用户停止",
        CampaignEndReason.ProtocolViolation => "缺少下一轮评定说明",
        _ => "供应商错误"
    };

    private static string EventKindName(CampaignEventKind kind) => kind switch
    {
        CampaignEventKind.GmOpening => "GM 开场",
        CampaignEventKind.PlayerIntent => "玩家行动",
        CampaignEventKind.GmResolution => "GM 裁定",
        CampaignEventKind.DiceRoll => "掷骰",
        CampaignEventKind.System => "系统",
        CampaignEventKind.StateDelta => "状态变化",
        _ => "私有传递"
    };

    private static string FlowName(CampaignFlowPreset flow) => flow switch
    {
        CampaignFlowPreset.CollaborativeTable => "协作圆桌",
        CampaignFlowPreset.BlindSubmission => "秘密同投",
        _ => "严格先攻"
    };

    private static string PhaseName(CampaignPhase phase) => phase switch
    {
        CampaignPhase.AwaitingActions => "等待行动",
        CampaignPhase.ReadyForResolution => "等待 GM 裁定",
        CampaignPhase.Resolving => "GM 裁定中",
        CampaignPhase.Paused => "已暂停",
        CampaignPhase.Completed => "已完成",
        _ => phase.ToString()
    };
}
