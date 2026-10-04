using System.Collections.ObjectModel;
using TavernDesk.App.Localization;
using TavernDesk.App.Presentation;
using TavernDesk.App.Services;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;

namespace TavernDesk.App.ViewModels;

public sealed record WorldbookScopeOption(
    WorldbookScopeKind Kind,
    string Label);

public sealed class WorldbookMountListItem
{
    public WorldbookMountListItem(
        WorldbookMount mount,
        IReadOnlyDictionary<string, string> characterNames,
        IReadOnlyDictionary<string, string> campaignNames)
    {
        Mount = mount;
        ScopeText = mount.ScopeKind switch
        {
            WorldbookScopeKind.Character => LanguageRuntime.Format(
                "Worldbook.Scope.CharacterFormat",
                characterNames.TryGetValue(mount.ScopeId, out var characterName)
                    ? characterName
                    : mount.ScopeId),
            WorldbookScopeKind.Campaign => LanguageRuntime.Format(
                "Worldbook.Scope.CampaignFormat",
                campaignNames.TryGetValue(mount.ScopeId, out var campaignName)
                    ? campaignName
                    : mount.ScopeId),
            _ => mount.ScopeText
        };
    }

    public WorldbookMount Mount { get; }
    public string ScopeText { get; }
}

public sealed class WorldbookCharacterBindingItem : ViewModelBase
{
    private bool _isBound;

    public WorldbookCharacterBindingItem(Character character, bool isBound)
    {
        Character = character;
        _isBound = isBound;
    }

    public Character Character { get; }
    public string Name => string.IsNullOrWhiteSpace(Character.Name)
        ? Character.Id
        : Character.Name;

    public bool IsBound
    {
        get => _isBound;
        set => SetProperty(ref _isBound, value);
    }
}

public sealed class WorldbookViewModel : ViewModelBase
{
    private readonly IWorldbookService _service;
    private readonly ICharacterRepository _charactersRepository;
    private readonly ICampaignScenarioRepository _campaignScenariosRepository;
    private readonly IFileDialogService _fileDialog;
    private readonly IUserInteractionService _interaction;
    private Worldbook? _selectedBook;
    private WorldbookEntry? _selectedEntry;
    private Character? _selectedCharacter;
    private CampaignScenario? _selectedCampaignScenario;
    private WorldbookScopeOption _selectedScopeOption;
    private string _entryTitle = string.Empty;
    private string _entryContent = string.Empty;
    private bool _loadingEntry;
    private bool _isSavingEntry;
    private readonly Dictionary<(string Book, string Entry), EntryEdit> _entryEdits = [];
    private sealed record EntryEdit(WorldbookEntry Original, string Title, string Content);
    private string _status =
        LanguageRuntime.GetString("Worldbook.Status.Intro");

    public WorldbookViewModel(
        IWorldbookService service,
        ICharacterRepository charactersRepository,
        ICampaignScenarioRepository campaignScenariosRepository,
        IFileDialogService fileDialog,
        IUserInteractionService interaction)
    {
        _service = service;
        _charactersRepository = charactersRepository;
        _campaignScenariosRepository = campaignScenariosRepository;
        _fileDialog = fileDialog;
        _interaction = interaction;
        ScopeOptions =
        [
            new(WorldbookScopeKind.Global, LanguageRuntime.GetString("Worldbook.Scope.Global")),
            new(WorldbookScopeKind.Character, LanguageRuntime.GetString("Worldbook.Scope.Character")),
            new(WorldbookScopeKind.Campaign, LanguageRuntime.GetString("Worldbook.Scope.Campaign"))
        ];
        _selectedScopeOption = ScopeOptions[0];

        ImportCommand = new AsyncRelayCommand(ImportAsync);
        DeleteCommand = new AsyncRelayCommand(
            DeleteAsync,
            () => SelectedBook is not null);
        RebuildIndexCommand = new AsyncRelayCommand(
            RebuildIndexAsync,
            () => SelectedBook is not null);
        SaveCharacterBindingsCommand = new AsyncRelayCommand(
            SaveCharacterBindingsAsync,
            () => SelectedBook is not null);
        SaveCampaignBindingsCommand = new AsyncRelayCommand(
            SaveCampaignBindingsAsync,
            () => SelectedBook is not null);
        SaveEntryTitleCommand = new AsyncRelayCommand(
            SaveEntryTitleAsync,
            () => SelectedBook is not null
                  && SelectedEntry is not null
                  && !string.IsNullOrWhiteSpace(EntryTitle)
                  && IsEntryTitleDirty);
        SelectAllCharacterBindingsCommand = new RelayCommand(
            () => SetAllCharacterBindings(true),
            () => SelectedBook is not null && CharacterBindings.Count > 0);
        ClearCharacterBindingsCommand = new RelayCommand(
            () => SetAllCharacterBindings(false),
            () => SelectedBook is not null && CharacterBindings.Count > 0);
        SelectAllCampaignBindingsCommand = new RelayCommand(
            () => SetAllCampaignBindings(true),
            () => SelectedBook is not null && CampaignBindings.Count > 0);
        ClearCampaignBindingsCommand = new RelayCommand(
            () => SetAllCampaignBindings(false),
            () => SelectedBook is not null && CampaignBindings.Count > 0);
        RefreshCommand = new AsyncRelayCommand(LoadAsync);
    }

    public ObservableCollection<Worldbook> Books { get; } = [];
    public ObservableCollection<WorldbookEntry> Entries { get; } = [];
    public ObservableCollection<WorldbookMountListItem> Mounts { get; } = [];
    public ObservableCollection<Character> Characters { get; } = [];
    public ObservableCollection<WorldbookCharacterBindingItem> CharacterBindings { get; } = [];
    public ObservableCollection<CampaignScenario> CampaignScenarios { get; } = [];
    public ObservableCollection<WorldbookCampaignBindingItem> CampaignBindings { get; } = [];
    public IReadOnlyList<WorldbookScopeOption> ScopeOptions { get; }

    public AsyncRelayCommand ImportCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }
    public AsyncRelayCommand RebuildIndexCommand { get; }
    public AsyncRelayCommand SaveCharacterBindingsCommand { get; }
    public AsyncRelayCommand SaveCampaignBindingsCommand { get; }
    public AsyncRelayCommand SaveEntryTitleCommand { get; }
    public RelayCommand SelectAllCharacterBindingsCommand { get; }
    public RelayCommand ClearCharacterBindingsCommand { get; }
    public RelayCommand SelectAllCampaignBindingsCommand { get; }
    public RelayCommand ClearCampaignBindingsCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }

    public Task RefreshSelectedBookAsync() =>
        SelectedBook is { } book
            ? LoadSelectedBookAsync(book.Id)
            : Task.CompletedTask;

    public Worldbook? SelectedBook
    {
        get => _selectedBook;
        set
        {
            if (!SetProperty(ref _selectedBook, value))
            {
                return;
            }

            Entries.Clear();
            Mounts.Clear();
            CharacterBindings.Clear();
            CampaignBindings.Clear();
            SelectedEntry = null;
            DeleteCommand.RaiseCanExecuteChanged();
            RebuildIndexCommand.RaiseCanExecuteChanged();
            SaveCharacterBindingsCommand.RaiseCanExecuteChanged();
            SaveCampaignBindingsCommand.RaiseCanExecuteChanged();
            SelectAllCharacterBindingsCommand.RaiseCanExecuteChanged();
            ClearCharacterBindingsCommand.RaiseCanExecuteChanged();
            SelectAllCampaignBindingsCommand.RaiseCanExecuteChanged();
            ClearCampaignBindingsCommand.RaiseCanExecuteChanged();
            if (value is not null)
            {
                _ = LoadSelectedBookAsync(value.Id);
            }
        }
    }

    public WorldbookEntry? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (!SetProperty(ref _selectedEntry, value))
            {
                return;
            }

            _loadingEntry = true;
            var edit = value is null ? null : _entryEdits.GetValueOrDefault((value.WorldbookId, value.Id));
            EntryTitle = edit?.Title ?? value?.Title ?? string.Empty;
            EntryContent = edit?.Content ?? value?.Content ?? string.Empty;
            _loadingEntry = false;
            OnPropertyChanged(nameof(IsEntryTitleDirty));
            SaveEntryTitleCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsEntryTitleDirty =>
        SelectedEntry is { } entry
        && _entryEdits.ContainsKey((entry.WorldbookId, entry.Id));

    public bool IsSavingEntry { get => _isSavingEntry; private set => SetProperty(ref _isSavingEntry, value); }

    public string EntryContent
    {
        get => _entryContent;
        set
        {
            if (SetProperty(ref _entryContent, value)) TrackEntryEdit();
        }
    }

    private void TrackEntryEdit()
    {
        if (_loadingEntry || SelectedEntry is not { } entry) return;
        var key = (entry.WorldbookId, entry.Id);
        var original = _entryEdits.GetValueOrDefault(key)?.Original ?? entry;
        if (EntryTitle.Trim() == original.Title && EntryContent == original.Content) _entryEdits.Remove(key);
        else _entryEdits[key] = new EntryEdit(original, EntryTitle, EntryContent);
        OnPropertyChanged(nameof(IsEntryTitleDirty));
        SaveEntryTitleCommand.RaiseCanExecuteChanged();
    }

    public string EntryTitle
    {
        get => _entryTitle;
        set
        {
            if (!SetProperty(ref _entryTitle, value))
            {
                return;
            }

            TrackEntryEdit();
        }
    }

    public async Task<bool> ConfirmCanLeaveAsync()
    {
        if (IsSavingEntry) return false;
        if (_entryEdits.Count == 0) return true;
        var decision = _interaction.ConfirmUnsavedWorldbookChanges(_entryEdits.Count);
        if (decision == UnsavedChangesDecision.Cancel) return false;
        if (decision == UnsavedChangesDecision.Save)
        {
            IsSavingEntry = true;
            try
            {
                foreach (var pair in _entryEdits.ToArray())
                {
                    await _service.UpdateEntryAsync(pair.Value.Original, pair.Value.Title, pair.Value.Content);
                    _entryEdits.Remove(pair.Key);
                }
            }
            catch (Exception exception)
            {
                Status = LanguageRuntime.Format("Worldbook.EntryNameSaveFailedFormat", LanguageRuntime.ErrorMessage(exception));
                return false;
            }
            finally
            {
                IsSavingEntry = false;
                OnPropertyChanged(nameof(IsEntryTitleDirty));
                SaveEntryTitleCommand.RaiseCanExecuteChanged();
            }
        }
        else _entryEdits.Clear();
        SelectedEntry = null;
        return true;
    }

    public WorldbookScopeOption SelectedScopeOption
    {
        get => _selectedScopeOption;
        set
        {
            if (SetProperty(ref _selectedScopeOption, value))
            {
                OnPropertyChanged(nameof(IsCharacterScope));
                OnPropertyChanged(nameof(IsCampaignScope));
            }
        }
    }

    public Character? SelectedCharacter
    {
        get => _selectedCharacter;
        set => SetProperty(ref _selectedCharacter, value);
    }

    public CampaignScenario? SelectedCampaignScenario
    {
        get => _selectedCampaignScenario;
        set => SetProperty(ref _selectedCampaignScenario, value);
    }

    public bool IsCharacterScope =>
        SelectedScopeOption.Kind == WorldbookScopeKind.Character;

    public bool IsCampaignScope =>
        SelectedScopeOption.Kind == WorldbookScopeKind.Campaign;

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public async Task LoadAsync()
    {
        var preferredBookId = SelectedBook?.Id;
        var characters = await _charactersRepository.ListAsync();
        var scenarios = await _campaignScenariosRepository.ListAsync();
        var books = await _service.ListAsync();
        Characters.Clear();
        foreach (var character in characters)
        {
            Characters.Add(character);
        }

        CampaignScenarios.Clear();
        foreach (var scenario in scenarios.OrderBy(item => item.Title))
        {
            CampaignScenarios.Add(scenario);
        }

        Books.Clear();
        foreach (var book in books)
        {
            Books.Add(book);
        }

        SelectAllCharacterBindingsCommand.RaiseCanExecuteChanged();
        ClearCharacterBindingsCommand.RaiseCanExecuteChanged();

        SelectedCharacter = SelectedCharacter is { } selected
                            && Characters.Any(item => item.Id == selected.Id)
            ? Characters.First(item => item.Id == selected.Id)
            : Characters.FirstOrDefault();
        SelectedCampaignScenario = SelectedCampaignScenario is { } selectedScenario
                                   && CampaignScenarios.Any(item => item.Id == selectedScenario.Id)
            ? CampaignScenarios.First(item => item.Id == selectedScenario.Id)
            : CampaignScenarios.FirstOrDefault();
        SelectedBook = Books.FirstOrDefault(book => book.Id == preferredBookId)
                       ?? Books.FirstOrDefault();
        Status = Books.Count == 0
            ? LanguageRuntime.GetString("Worldbook.Empty")
            : LanguageRuntime.Format("Worldbook.LoadedFormat", Books.Count);
    }

    private async Task LoadSelectedBookAsync(string worldbookId)
    {
        try
        {
            var entries = await _service.ListEntriesAsync(worldbookId);
            var mounts = await GetMountsAsync(worldbookId);
            if (!string.Equals(SelectedBook?.Id, worldbookId, StringComparison.Ordinal))
            {
                return;
            }

            Entries.Clear();
            foreach (var entry in entries)
            {
                Entries.Add(entry);
            }

            Mounts.Clear();
            CharacterBindings.Clear();
            CampaignBindings.Clear();
            var characterNames = Characters
                .GroupBy(character => character.Id, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.First().Name,
                    StringComparer.Ordinal);
            var campaignNames = CampaignScenarios
                .GroupBy(scenario => scenario.Id, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.First().Title,
                    StringComparer.Ordinal);
            foreach (var mount in mounts)
            {
                Mounts.Add(new WorldbookMountListItem(
                    mount,
                    characterNames,
                    campaignNames));
            }

            var boundCharacterIds = mounts
                .Where(mount => mount.ScopeKind == WorldbookScopeKind.Character
                                && mount.IsEnabled)
                .Select(mount => mount.ScopeId)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var character in Characters)
            {
                CharacterBindings.Add(
                    new WorldbookCharacterBindingItem(
                        character,
                        boundCharacterIds.Contains(character.Id)));
            }

            var boundCampaignIds = mounts
                .Where(mount => mount.ScopeKind == WorldbookScopeKind.Campaign
                                && mount.IsEnabled)
                .Select(mount => mount.ScopeId)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var scenario in CampaignScenarios.OrderBy(item => item.Title))
            {
                CampaignBindings.Add(
                    new WorldbookCampaignBindingItem(
                        scenario,
                        boundCampaignIds.Contains(scenario.Id)));
            }

            SelectAllCharacterBindingsCommand.RaiseCanExecuteChanged();
            ClearCharacterBindingsCommand.RaiseCanExecuteChanged();
            SaveCampaignBindingsCommand.RaiseCanExecuteChanged();
            SelectAllCampaignBindingsCommand.RaiseCanExecuteChanged();
            ClearCampaignBindingsCommand.RaiseCanExecuteChanged();
        }
        catch (Exception exception)
        {
            Status = LanguageRuntime.Format("Worldbook.EntryReadFailedFormat", LanguageRuntime.ErrorMessage(exception));
        }
    }

    private Task<IReadOnlyList<WorldbookMount>> GetMountsAsync(string worldbookId) =>
        _service.ListMountsAsync(worldbookId);

    private void SetAllCharacterBindings(bool isBound)
    {
        foreach (var item in CharacterBindings)
        {
            item.IsBound = isBound;
        }
    }

    private void SetAllCampaignBindings(bool isBound)
    {
        foreach (var item in CampaignBindings)
        {
            item.IsBound = isBound;
        }
    }

    private async Task SaveCharacterBindingsAsync()
    {
        if (SelectedBook is not { } book)
        {
            return;
        }

        try
        {
            var existingMounts = await _service.ListMountsAsync(book.Id);
            var existingCharacterMounts = existingMounts
                .Where(mount => mount.ScopeKind == WorldbookScopeKind.Character)
                .ToDictionary(mount => mount.ScopeId, StringComparer.Ordinal);
            var nextSortIndex = existingCharacterMounts.Values
                .Select(mount => mount.SortIndex)
                .DefaultIfEmpty(90)
                .Max();
            var desiredMounts = new List<WorldbookMount>();

            foreach (var item in CharacterBindings.Where(item => item.IsBound))
            {
                var sortIndex = existingCharacterMounts.TryGetValue(
                    item.Character.Id,
                    out var existing)
                    ? existing.SortIndex
                    : nextSortIndex += 10;
                desiredMounts.Add(
                    new WorldbookMount
                    {
                        WorldbookId = book.Id,
                        ScopeKind = WorldbookScopeKind.Character,
                        ScopeId = item.Character.Id,
                        SortIndex = sortIndex,
                        IsEnabled = true,
                        MountedRevision = book.Revision
                    });
            }

            await _service.ReplaceCharacterMountsAsync(book.Id, desiredMounts);
            await LoadSelectedBookAsync(book.Id);
            Status = desiredMounts.Count == 0
                ? LanguageRuntime.Format("Worldbook.CharacterBindingsClearedFormat", book.Name)
                : LanguageRuntime.Format(
                    "Worldbook.CharacterBindingsSavedFormat",
                    book.Name,
                    desiredMounts.Count);
        }
        catch (Exception exception)
        {
            Status = LanguageRuntime.Format("Worldbook.CharacterBindingsFailedFormat", LanguageRuntime.ErrorMessage(exception));
        }
    }

    private async Task SaveCampaignBindingsAsync()
    {
        if (SelectedBook is not { } book)
        {
            return;
        }

        try
        {
            var desiredMounts = CampaignBindings
                .Where(item => item.IsBound)
                .Select((item, index) => new WorldbookMount
                {
                    WorldbookId = book.Id,
                    ScopeKind = WorldbookScopeKind.Campaign,
                    ScopeId = item.Scenario.Id,
                    SortIndex = 100 + index * 10,
                    IsEnabled = true,
                    MountedRevision = book.Revision
                })
                .ToArray();
            await _service.ReplaceScopeMountsAsync(
                book.Id,
                WorldbookScopeKind.Campaign,
                desiredMounts);
            await LoadSelectedBookAsync(book.Id);
            Status = desiredMounts.Length == 0
                ? LanguageRuntime.Format("Worldbook.CampaignBindingsClearedFormat", book.Name)
                : LanguageRuntime.Format(
                    "Worldbook.CampaignBindingsSavedFormat",
                    book.Name,
                    desiredMounts.Length);
        }
        catch (Exception exception)
        {
            Status = LanguageRuntime.Format("Worldbook.CampaignBindingsFailedFormat", LanguageRuntime.ErrorMessage(exception));
        }
    }

    private async Task SaveEntryTitleAsync()
    {
        if (SelectedBook is not { } book
            || SelectedEntry is not { } entry)
        {
            return;
        }

        var title = EntryTitle.Trim();
        if (title.Length == 0)
        {
            Status = LanguageRuntime.GetString("Worldbook.EntryNameRequired");
            return;
        }

        try
        {
            IsSavingEntry = true;
            var edit = _entryEdits.GetValueOrDefault((book.Id, entry.Id));
            if (edit is null) return;
            await _service.UpdateEntryAsync(edit.Original, title, edit.Content);
            _entryEdits.Remove((book.Id, entry.Id));
            await LoadSelectedBookAsync(book.Id);
            if (!string.Equals(SelectedBook?.Id, book.Id, StringComparison.Ordinal))
            {
                return;
            }

            SelectedEntry = Entries.FirstOrDefault(item => item.Id == entry.Id);
            Status = LanguageRuntime.Format("Worldbook.EntryNameSavedFormat", title);
        }
        catch (Exception exception)
        {
            Status = LanguageRuntime.Format("Worldbook.EntryNameSaveFailedFormat", LanguageRuntime.ErrorMessage(exception));
        }
        finally { IsSavingEntry = false; }
    }

    private async Task ImportAsync()
    {
        var path = _fileDialog.PickWorldbookSource();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (IsCharacterScope && SelectedCharacter is null)
        {
            Status = LanguageRuntime.GetString("Worldbook.SelectCharacter");
            return;
        }

        if (IsCampaignScope && SelectedCampaignScenario is null)
        {
            Status = LanguageRuntime.GetString("Worldbook.SelectCampaign");
            return;
        }

        try
        {
            var scopeId = IsCharacterScope
                ? SelectedCharacter?.Id
                : IsCampaignScope
                    ? SelectedCampaignScenario?.Id
                    : null;
            var result = await _service.ImportAsync(
                path,
                SelectedScopeOption.Kind,
                scopeId);
            await LoadAsync();
            SelectedBook = Books.FirstOrDefault(book => book.Id == result.Worldbook.Id);
            Status = result.Warnings.Count == 0
                ? LanguageRuntime.Format(
                    "Worldbook.ImportedFormat",
                    result.Worldbook.Name,
                    result.Entries.Count)
                : LanguageRuntime.Format(
                    "Worldbook.ImportedWithWarningsFormat",
                    result.Worldbook.Name,
                    result.Entries.Count,
                    result.Warnings.Count);
        }
        catch (Exception exception)
        {
            Status = LanguageRuntime.Format("Worldbook.ImportFailedFormat", LanguageRuntime.ErrorMessage(exception));
        }
    }

    private async Task DeleteAsync()
    {
        if (SelectedBook is not { } book
            || !_interaction.ConfirmWorldbookDeletion(book.Name))
        {
            return;
        }

        try
        {
            await _service.DeleteAsync(book.Id);
            await LoadAsync();
            Status = LanguageRuntime.Format("Worldbook.DeletedFormat", book.Name);
        }
        catch (Exception exception)
        {
            Status = LanguageRuntime.Format("Worldbook.DeleteFailedFormat", LanguageRuntime.ErrorMessage(exception));
        }
    }

    private async Task RebuildIndexAsync()
    {
        if (SelectedBook is not { } book)
        {
            return;
        }

        try
        {
            var result = await _service.RebuildIndexAsync(book.Id);
            await LoadAsync();
            SelectedBook = Books.FirstOrDefault(item => item.Id == book.Id);
            Status = LanguageRuntime.Format(
                "Worldbook.IndexRebuiltFormat",
                result.ChunkCount,
                result.EmbeddingDimension);
        }
        catch (Exception exception)
        {
            Status = LanguageRuntime.Format("Worldbook.RebuildFailedFormat", LanguageRuntime.ErrorMessage(exception));
        }
    }

}
