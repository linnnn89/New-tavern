using System.Collections.ObjectModel;
using TavernDesk.App.Localization;
using TavernDesk.App.Presentation;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;

namespace TavernDesk.App.ViewModels;

public sealed class CampaignScenarioEditorViewModel : ViewModelBase
{
    private readonly ICampaignScenarioRepository _scenarios;
    private readonly IWorldbookService? _worldbooks;
    private CampaignScenario? _source;
    private bool _isCreatingScenario;
    private string _scenarioTitle = string.Empty;
    private string _scenarioSummary = string.Empty;
    private string _scenarioWorldSetting = string.Empty;
    private string _scenarioPublicRules = string.Empty;
    private string _scenarioGmInstructions = string.Empty;
    private CampaignNarrativePermissionChoice
        _scenarioNewNpcPermission = null!;
    private CampaignNarrativePermissionChoice
        _scenarioRelationshipChangePermission = null!;
    private CampaignNarrativePermissionChoice
        _scenarioIndependentPlotPermission = null!;
    private string _scenarioOpeningSetup = string.Empty;
    private string _scenarioOpeningNarration = string.Empty;
    private string _scenarioLegacyExamplesArchive = string.Empty;

    public CampaignScenarioEditorViewModel(
        ICampaignScenarioRepository scenarios, IWorldbookService? worldbooks = null)
    {
        _scenarios = scenarios;
        _worldbooks = worldbooks;
        NarrativePermissionChoices =
        [
            new CampaignNarrativePermissionChoice(
                CampaignNarrativePermission.Forbidden,
                LanguageRuntime.GetString("Campaigns.Permission.Forbidden"),
                LanguageRuntime.GetString("Campaigns.Permission.ForbiddenHelp")),
            new CampaignNarrativePermissionChoice(
                CampaignNarrativePermission.PlayerIntentOnly,
                LanguageRuntime.GetString("Campaigns.Permission.PlayerIntent"),
                LanguageRuntime.GetString("Campaigns.Permission.PlayerIntentHelp")),
            new CampaignNarrativePermissionChoice(
                CampaignNarrativePermission.GmDiscretion,
                LanguageRuntime.GetString("Campaigns.Permission.GmDiscretion"),
                LanguageRuntime.GetString("Campaigns.Permission.GmDiscretionHelp"))
        ];
        _scenarioNewNpcPermission = NarrativePermissionChoices[2];
        _scenarioRelationshipChangePermission = NarrativePermissionChoices[1];
        _scenarioIndependentPlotPermission = NarrativePermissionChoices[1];
    }

    public ObservableCollection<CampaignWorldbookBindingItem> ScenarioWorldbookBindings { get; } = [];
    public IReadOnlyList<CampaignNarrativePermissionChoice> NarrativePermissionChoices { get; }

    public bool IsCreatingScenario
    {
        get => _isCreatingScenario;
        private set
        {
            if (!SetProperty(ref _isCreatingScenario, value)) return;
            OnPropertyChanged(nameof(ScenarioEditorTitle));
            OnPropertyChanged(nameof(ScenarioEditorDescription));
        }
    }
    public string ScenarioEditorTitle =>
        IsCreatingScenario
            ? LanguageRuntime.GetString("Campaigns.Scenario.NewTitle")
            : LanguageRuntime.GetString("Campaigns.Scenario.EditTitle");
    public string ScenarioEditorDescription => IsCreatingScenario
        ? LanguageRuntime.GetString("Campaigns.Scenario.NewDescription")
        : LanguageRuntime.GetString("Campaigns.Scenario.EditDescription");

    public string ScenarioTitle
    {
        get => _scenarioTitle;
        set => SetProperty(ref _scenarioTitle, value);
    }

    public string ScenarioSummary
    {
        get => _scenarioSummary;
        set => SetProperty(ref _scenarioSummary, value);
    }

    public string ScenarioWorldSetting
    {
        get => _scenarioWorldSetting;
        set => SetProperty(ref _scenarioWorldSetting, value);
    }

    public string ScenarioPublicRules
    {
        get => _scenarioPublicRules;
        set => SetProperty(ref _scenarioPublicRules, value);
    }

    public string ScenarioGmInstructions
    {
        get => _scenarioGmInstructions;
        set => SetProperty(ref _scenarioGmInstructions, value);
    }

    public string ScenarioOpeningSetup
    {
        get => _scenarioOpeningSetup;
        set => SetProperty(ref _scenarioOpeningSetup, value);
    }

    public string ScenarioOpeningNarration
    {
        get => _scenarioOpeningNarration;
        set => SetProperty(ref _scenarioOpeningNarration, value);
    }

    public string ScenarioLegacyExamplesArchive
    {
        get => _scenarioLegacyExamplesArchive;
        set => SetProperty(ref _scenarioLegacyExamplesArchive, value);
    }

    public CampaignNarrativePermissionChoice ScenarioNewNpcPermission
    {
        get => _scenarioNewNpcPermission;
        set => SetProperty(ref _scenarioNewNpcPermission, value);
    }

    public CampaignNarrativePermissionChoice ScenarioRelationshipChangePermission
    {
        get => _scenarioRelationshipChangePermission;
        set => SetProperty(ref _scenarioRelationshipChangePermission, value);
    }

    public CampaignNarrativePermissionChoice ScenarioIndependentPlotPermission
    {
        get => _scenarioIndependentPlotPermission;
        set => SetProperty(ref _scenarioIndependentPlotPermission, value);
    }

    public async Task LoadAsync(CampaignScenario scenario, bool isCreating = false)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        _source = scenario;
        IsCreatingScenario = isCreating;
        ScenarioTitle = scenario.Title;
        ScenarioSummary = scenario.Summary;
        ScenarioWorldSetting = scenario.WorldSetting;
        ScenarioPublicRules = scenario.PublicRules;
        ScenarioGmInstructions = scenario.GmInstructions;
        ScenarioNewNpcPermission = FindNarrativePermission(
            scenario.NewNpcPermission);
        ScenarioRelationshipChangePermission = FindNarrativePermission(
            scenario.RelationshipChangePermission);
        ScenarioIndependentPlotPermission = FindNarrativePermission(
            scenario.IndependentPlotPermission);
        ScenarioOpeningSetup = scenario.OpeningSetup;
        ScenarioOpeningNarration = scenario.OpeningNarration;
        ScenarioLegacyExamplesArchive = scenario.LegacyExamplesArchive;
        await LoadScenarioWorldbookBindingsAsync(scenario.Id);
    }

    private async Task LoadScenarioWorldbookBindingsAsync(string scenarioId)
    {
        ScenarioWorldbookBindings.Clear();
        if (_worldbooks is null)
        {
            return;
        }

        var books = await _worldbooks.ListAsync();
        var mounts = await Task.WhenAll(
            books.Select(book => _worldbooks.ListMountsAsync(book.Id)));
        var boundBookIds = mounts
            .SelectMany(item => item)
            .Where(mount => mount.ScopeKind == WorldbookScopeKind.Campaign
                            && mount.IsEnabled
                            && mount.ScopeId == scenarioId)
            .Select(mount => mount.WorldbookId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var book in books.OrderBy(item => item.Name))
        {
            ScenarioWorldbookBindings.Add(
                new CampaignWorldbookBindingItem(
                    book,
                    boundBookIds.Contains(book.Id)));
        }
    }

    public async Task<CampaignScenario> SaveAsync(CancellationToken cancellationToken = default)
    {
        if (_source is null)
            throw new InvalidOperationException(LanguageRuntime.GetString("Campaigns.Scenario.NoEditor"));
        if (string.IsNullOrWhiteSpace(ScenarioTitle))
            throw new InvalidOperationException(LanguageRuntime.GetString("Campaigns.Scenario.TitleRequired"));

        // Save a detached draft so a failed transaction cannot mutate the library item.
        var draft = new CampaignScenario
        {
            Id = _source.Id,
            CreatedAt = _source.CreatedAt,
            UpdatedAt = _source.UpdatedAt,
            SourceCardJson = _source.SourceCardJson,
            SourceFileName = _source.SourceFileName,
            CoverPath = _source.CoverPath,
            Title = ScenarioTitle.Trim(),
            Summary = ScenarioSummary.Trim(),
            WorldSetting = ScenarioWorldSetting.Trim(),
            PublicRules = ScenarioPublicRules.Trim(),
            GmInstructions = ScenarioGmInstructions.Trim(),
            NewNpcPermission = ScenarioNewNpcPermission.Value,
            RelationshipChangePermission = ScenarioRelationshipChangePermission.Value,
            IndependentPlotPermission = ScenarioIndependentPlotPermission.Value,
            OpeningSetup = ScenarioOpeningSetup.Trim(),
            OpeningNarration = ScenarioOpeningNarration.Trim(),
            LegacyExamplesArchive = ScenarioLegacyExamplesArchive.Trim()
        };
        var bindings = ScenarioWorldbookBindings.Select(item => new CampaignScenarioWorldbookBinding(
            item.Worldbook.Id, item.IsBound, item.Worldbook.Revision)).ToArray();
        await _scenarios.SaveWithWorldbookBindingsAsync(draft, bindings, cancellationToken);
        _source = draft;
        return draft;
    }

    public void EndEdit()
    {
        _source = null;
        IsCreatingScenario = false;
    }

    private CampaignNarrativePermissionChoice FindNarrativePermission(
        CampaignNarrativePermission value) =>
        NarrativePermissionChoices.First(item => item.Value == value);
}
