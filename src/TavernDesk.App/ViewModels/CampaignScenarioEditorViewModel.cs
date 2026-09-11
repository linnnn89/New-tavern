using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Threading;
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
    private readonly ICampaignScenarioDraftRepository? _drafts;
    private readonly SemaphoreSlim _draftGate = new(1, 1);
    private readonly DispatcherTimer _draftTimer;
    private bool _loading;
    private bool _dirty;
    private string _baselineHash = "";
    private string _draftId = "";
    private DateTimeOffset? _baseUpdatedAt;
    private string _draftStatus = "";
    public bool HasActiveEdit => _source is not null;
    public string DraftStatus { get => _draftStatus; private set => SetProperty(ref _draftStatus, value); }
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
        _drafts = scenarios as ICampaignScenarioDraftRepository;
        _draftTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _draftTimer.Tick += async (_, _) =>
        {
            if (!_dirty || _loading) return;
            try { await FlushDraftAsync(); }
            catch (Exception exception) { DraftStatus = LanguageRuntime.Format("Recovery.Failed", exception.Message); }
        };
        PropertyChanged += (_, args) =>
        {
            if (!_loading && HasActiveEdit && args.PropertyName?.StartsWith("Scenario", StringComparison.Ordinal) == true)
                _dirty = true;
        };
        ScenarioWorldbookBindings.CollectionChanged += (_, args) =>
        {
            if (args.NewItems is not null)
                foreach (CampaignWorldbookBindingItem item in args.NewItems)
                    item.PropertyChanged += (_, _) => { if (!_loading) _dirty = true; };
            if (!_loading) _dirty = true;
        };
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
        await FlushDraftAsync();
        _loading = true;
        try
        {
            _source = scenario;
            _draftId = scenario.Id;
            _baseUpdatedAt = isCreating ? null : scenario.UpdatedAt;
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
            _baselineHash = ContentHash(CaptureDraft());
            _dirty = false;
            DraftStatus = LanguageRuntime.GetString("Recovery.Ready");
            _draftTimer.Start();
        }
        finally { _loading = false; }
    }

    public async Task RestoreDraftAsync(CampaignScenarioEditDraft draft)
    {
        await LoadAsync(draft.Scenario, draft.BaseUpdatedAt is null);
        _loading = true;
        try
        {
            foreach (var item in ScenarioWorldbookBindings)
                item.IsBound = draft.Bindings.Any(binding => binding.WorldbookId == item.Worldbook.Id && binding.IsBound);
            _draftId = draft.Id;
            _baseUpdatedAt = draft.BaseUpdatedAt;
            _baselineHash = draft.BaselineHash;
            // If the original changed or disappeared, keep both versions by saving the recovery under a new identity.
            var original = await _scenarios.GetAsync(draft.Id);
            if (original?.UpdatedAt != draft.BaseUpdatedAt)
            {
                _source = CreateScenario(trim: false, id: Guid.NewGuid().ToString("N"));
                IsCreatingScenario = true;
                DraftStatus = LanguageRuntime.GetString("Recovery.ConflictCopy");
            }
            else DraftStatus = LanguageRuntime.GetString("Recovery.Restored");
            _dirty = false;
        }
        finally { _loading = false; }
    }

    public async Task FlushDraftAsync()
    {
        // Closing must also wait for a write already in flight, even after it captured the dirty flag.
        if (_drafts is null || !HasActiveEdit || _loading) return;
        await _draftGate.WaitAsync();
        try
        {
            if (!HasActiveEdit || !_dirty || _loading) return;
            var draft = CaptureDraft();
            _dirty = false;
            try
            {
                if (ContentHash(draft) == _baselineHash)
                    await Task.Run(() => _drafts.DeleteEditDraftAsync(draft.Id));
                else
                    await Task.Run(() => _drafts.WriteEditDraftAsync(draft));
                DraftStatus = LanguageRuntime.GetString("Recovery.Saved");
            }
            catch { _dirty = true; throw; }
        }
        finally { _draftGate.Release(); }
    }

    public async Task DiscardDraftAsync()
    {
        await _draftGate.WaitAsync();
        try
        {
            if (_drafts is not null && HasActiveEdit)
                await Task.Run(() => _drafts.DeleteEditDraftAsync(_draftId));
            EndEdit();
        }
        finally { _draftGate.Release(); }
    }

    private static string ContentHash(CampaignScenarioEditDraft draft)
    {
        var scenario = draft.Scenario;
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            scenario.Title, scenario.Summary, scenario.WorldSetting, scenario.PublicRules,
            scenario.GmInstructions, scenario.NewNpcPermission, scenario.RelationshipChangePermission,
            scenario.IndependentPlotPermission, scenario.OpeningSetup, scenario.OpeningNarration,
            scenario.LegacyExamplesArchive, draft.Bindings
        })));
    }

    private CampaignScenarioEditDraft CaptureDraft()
    {
        var scenario = CreateScenario(trim: false);
        var bindings = ScenarioWorldbookBindings.Select(item => new CampaignScenarioWorldbookBinding(
            item.Worldbook.Id, item.IsBound, item.Worldbook.Revision)).ToArray();
        return new(_draftId, scenario, bindings, _baseUpdatedAt, _baselineHash, DateTimeOffset.UtcNow);
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

        await FlushDraftAsync();
        await _draftGate.WaitAsync(cancellationToken);
        try
        {
            var recovery = CaptureDraft() with { Scenario = CreateScenario(trim: true) };
            if (_drafts is not null)
                await Task.Run(() => _drafts.CommitEditDraftAsync(recovery, cancellationToken), cancellationToken);
            else
                await _scenarios.SaveWithWorldbookBindingsAsync(recovery.Scenario, recovery.Bindings, cancellationToken);
            _source = recovery.Scenario;
            _baseUpdatedAt = _source.UpdatedAt;
            _draftId = _source.Id;
            _baselineHash = ContentHash(CaptureDraft());
            _dirty = false;
            return _source;
        }
        finally { _draftGate.Release(); }
    }

    private CampaignScenario CreateScenario(bool trim, string? id = null)
    {
        string Text(string value) => trim ? value.Trim() : value;
        return new CampaignScenario
        {
            Id = id ?? _source!.Id,
            CreatedAt = _source!.CreatedAt,
            UpdatedAt = _source.UpdatedAt,
            SourceCardJson = _source.SourceCardJson,
            SourceFileName = _source.SourceFileName,
            CoverPath = _source.CoverPath,
            Title = Text(ScenarioTitle),
            Summary = Text(ScenarioSummary),
            WorldSetting = Text(ScenarioWorldSetting),
            PublicRules = Text(ScenarioPublicRules),
            GmInstructions = Text(ScenarioGmInstructions),
            NewNpcPermission = ScenarioNewNpcPermission.Value,
            RelationshipChangePermission = ScenarioRelationshipChangePermission.Value,
            IndependentPlotPermission = ScenarioIndependentPlotPermission.Value,
            OpeningSetup = Text(ScenarioOpeningSetup),
            OpeningNarration = Text(ScenarioOpeningNarration),
            LegacyExamplesArchive = Text(ScenarioLegacyExamplesArchive)
        };
    }

    public void EndEdit()
    {
        _draftTimer.Stop();
        _dirty = false;
        _source = null;
        IsCreatingScenario = false;
    }

    private CampaignNarrativePermissionChoice FindNarrativePermission(
        CampaignNarrativePermission value) =>
        NarrativePermissionChoices.First(item => item.Value == value);
}
