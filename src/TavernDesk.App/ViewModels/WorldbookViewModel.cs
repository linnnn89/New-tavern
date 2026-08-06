using System.Collections.ObjectModel;
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
            WorldbookScopeKind.Character => $"角色：{(characterNames.TryGetValue(
                mount.ScopeId,
                out var characterName)
                ? characterName
                : mount.ScopeId)}",
            WorldbookScopeKind.Campaign => $"跑团剧本：{(campaignNames.TryGetValue(
                mount.ScopeId,
                out var campaignName)
                ? campaignName
                : mount.ScopeId)}",
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
    private string _status =
        "支持酒馆独立 world_info JSON，以及 PNG/JSON/CHARX 角色卡内置世界书；导入不会修改原文件。";

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
            new(WorldbookScopeKind.Global, "挂载到全局"),
            new(WorldbookScopeKind.Character, "绑定到角色"),
            new(WorldbookScopeKind.Campaign, "绑定到跑团剧本")
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
                  && !string.IsNullOrWhiteSpace(EntryTitle));
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

            EntryTitle = value?.Title ?? string.Empty;
            SaveEntryTitleCommand.RaiseCanExecuteChanged();
        }
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

            SaveEntryTitleCommand.RaiseCanExecuteChanged();
        }
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
            ? "尚未导入世界书。先导入独立 JSON，或选择包含 character_book 的角色卡。"
            : $"已加载 {Books.Count} 本世界书；关键词触发始终独立于语义索引。";
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
            Status = $"读取世界书条目失败：{exception.Message}";
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
                ? $"已清除“{book.Name}”的角色绑定；全局及其他范围挂载未改变。"
                : $"已将“{book.Name}”绑定到 {desiredMounts.Count} 个角色；全局及其他范围挂载未改变。";
        }
        catch (Exception exception)
        {
            Status = $"保存角色绑定失败：{exception.Message}";
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
                ? $"已清除“{book.Name}”的跑团剧本绑定。"
                : $"已将“{book.Name}”绑定到 {desiredMounts.Length} 个跑团剧本。";
        }
        catch (Exception exception)
        {
            Status = $"保存跑团剧本绑定失败：{exception.Message}";
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
            Status = "词条名不能为空。";
            return;
        }

        try
        {
            await _service.UpdateEntryTitleAsync(book.Id, entry.Id, title);
            await LoadSelectedBookAsync(book.Id);
            if (!string.Equals(SelectedBook?.Id, book.Id, StringComparison.Ordinal))
            {
                return;
            }

            SelectedEntry = Entries.FirstOrDefault(item => item.Id == entry.Id);
            Status = $"已保存词条名“{title}”；原始来源文件未修改。若要让新标题进入 FTS/向量索引，请点击“重建 Embedding 索引”。";
        }
        catch (Exception exception)
        {
            Status = $"保存词条名失败：{exception.Message}";
        }
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
            Status = "请选择要绑定的角色。";
            return;
        }

        if (IsCampaignScope && SelectedCampaignScenario is null)
        {
            Status = "请选择要绑定的跑团剧本。";
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
                ? $"已导入“{result.Worldbook.Name}”，共 {result.Entries.Count} 个条目；原文件未修改。"
                : $"已导入“{result.Worldbook.Name}”，共 {result.Entries.Count} 个条目；有 {result.Warnings.Count} 条兼容性提示。";
        }
        catch (Exception exception)
        {
            Status = $"导入世界书失败：{exception.Message}";
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
            Status = $"已删除世界书“{book.Name}”；原始来源文件未修改。";
        }
        catch (Exception exception)
        {
            Status = $"删除世界书失败：{exception.Message}";
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
            Status = string.Join(" ", result.Diagnostics);
        }
        catch (Exception exception)
        {
            Status = $"重建 Embedding 索引失败：{exception.Message}";
        }
    }

}
