using System.Collections;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using TavernDesk.App.Presentation;
using TavernDesk.App.Services;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;

namespace TavernDesk.App.ViewModels;

public sealed class CharactersViewModel : ViewModelBase
{
    private readonly ICharacterRepository _repository;
    private readonly ICharacterShelfRepository _shelves;
    private readonly IConversationRepository _conversations;
    private readonly ICharacterCardLibrary _cardLibrary;
    private readonly IFileDialogService _fileDialog;
    private readonly IAppSettingsRepository _settings;
    private readonly IUserInteractionService _interaction;
    private readonly Func<Character, Task> _startChat;
    private readonly Func<Character, Task> _createNewChat;
    private readonly Func<ConversationSummary, Task> _openConversation;
    private IReadOnlySet<string> _selectedShelfCharacterIds =
        new HashSet<string>(StringComparer.Ordinal);
    private CharacterCardScale _scale = CharacterCardScale.Medium;
    private string _status = "支持 PNG、JSON 与 CHARX；导入采用本地工作副本，不修改原文件。";
    private string _searchText = string.Empty;
    private readonly CharacterEditBuffer _inactiveEditor = new();
    private readonly ObservableCollection<CharacterConversationListItemViewModel>
        _inactiveCharacterConversations = [];
    private CharacterDetailSession? _characterSession;
    private CharacterShelfListItemViewModel? _selectedShelf;
    private CharacterShelfListItemViewModel? _membershipShelf;
    private string _importReportText = "选择角色后可查看导入报告。";
    private int _shelfSelectionVersion;
    private bool _isShelfBatchMode;

    public CharactersViewModel(
        ICharacterRepository repository,
        ICharacterShelfRepository shelves,
        IConversationRepository conversations,
        ICharacterCardLibrary cardLibrary,
        IAppSettingsRepository settings,
        IFileDialogService fileDialog,
        IUserInteractionService interaction,
        Func<Character, Task> startChat,
        Func<Character, Task> createNewChat,
        Func<ConversationSummary, Task> openConversation)
    {
        _repository = repository;
        _shelves = shelves;
        _conversations = conversations;
        _cardLibrary = cardLibrary;
        _settings = settings;
        _fileDialog = fileDialog;
        _interaction = interaction;
        _startChat = startChat;
        _createNewChat = createNewChat;
        _openConversation = openConversation;

        SetDenseCommand = new AsyncRelayCommand(() => SetScaleAsync(CharacterCardScale.Dense));
        SetMediumCommand = new AsyncRelayCommand(() => SetScaleAsync(CharacterCardScale.Medium));
        SetLargeCommand = new AsyncRelayCommand(() => SetScaleAsync(CharacterCardScale.Large));
        ImportCommand = new AsyncRelayCommand(ImportAsync);
        ExportCommand = new AsyncRelayCommand(
            ExportAsync,
            () => SelectedCharacter is not null);
        SaveCharacterCommand = new AsyncRelayCommand(
            SaveCharacterAsync,
            () => SelectedCharacter is not null && Editor.IsDirty);
        EditRawCardJsonCommand = new AsyncRelayCommand(
            EditRawCardJsonAsync,
            () => SelectedCharacter is not null);
        StartChatCommand = new AsyncRelayCommand(StartChatAsync);
        CreateNewChatCommand = new AsyncRelayCommand(CreateNewChatAsync);
        EditCharacterCommand = new AsyncRelayCommand(OpenCharacterEditorAsync);
        OpenCharacterToolsCommand = new AsyncRelayCommand(OpenCharacterOverviewAsync);
        ShowCharacterEditorCommand = new AsyncRelayCommand(ShowCharacterEditorAsync);
        ShowCharacterOverviewCommand = new AsyncRelayCommand(ShowCharacterOverviewAsync);
        ToggleClassificationCommand = new AsyncRelayCommand(ToggleClassificationAsync);
        ReplaceCharacterAvatarCommand = new AsyncRelayCommand(
            ReplaceCharacterAvatarAsync,
            () => SelectedCharacter is not null);
        DeleteCharacterCommand = new AsyncRelayCommand(
            DeleteCharacterAsync,
            () => SelectedCharacter is not null);
        OpenCharacterConversationCommand = new AsyncRelayCommand(OpenCharacterConversationAsync);
        CloseCharacterToolsCommand = new AsyncRelayCommand(CloseToolsAsync);
        CreateShelfCommand = new AsyncRelayCommand(CreateShelfAsync);
        RenameShelfCommand = new AsyncRelayCommand(
            RenameShelfAsync,
            () => SelectedShelf is { IsAllCharacters: false });
        DeleteShelfCommand = new AsyncRelayCommand(
            DeleteShelfAsync,
            () => SelectedShelf is { IsAllCharacters: false });
        ToggleShelfBatchModeCommand = new RelayCommand(
            ToggleShelfBatchMode,
            _ => IsCustomShelfSelected);
        AddToShelfCommand = new AsyncRelayCommand(
            AddToShelfAsync,
            () => SelectedCharacter is not null
                  && MembershipShelf is not null);
        RemoveFromShelfCommand = new AsyncRelayCommand(
            RemoveFromShelfAsync,
            () => SelectedCharacter is not null
                  && MembershipShelf is not null);
        RemoveSelectedFromShelfCommand = new AsyncRelayCommand(
            RemoveSelectedFromShelfAsync,
            _ => IsShelfBatchMode);

        Editor.PropertyChanged += OnEditorPropertyChanged;
    }

    public ObservableCollection<Character> Characters { get; } = [];
    public ObservableCollection<Character> VisibleCharacters { get; } = [];
    public ObservableCollection<CharacterShelfListItemViewModel> ShelfItems { get; } = [];
    public ObservableCollection<CharacterShelfListItemViewModel> CustomShelfItems { get; } = [];
    public ObservableCollection<CharacterConversationListItemViewModel>
        CharacterConversations =>
        _characterSession?.Conversations ?? _inactiveCharacterConversations;
    public CharacterEditBuffer Editor =>
        _characterSession?.Editor ?? _inactiveEditor;
    public AsyncRelayCommand SetDenseCommand { get; }
    public AsyncRelayCommand SetMediumCommand { get; }
    public AsyncRelayCommand SetLargeCommand { get; }
    public AsyncRelayCommand ImportCommand { get; }
    public AsyncRelayCommand ExportCommand { get; }
    public AsyncRelayCommand SaveCharacterCommand { get; }
    public AsyncRelayCommand EditRawCardJsonCommand { get; }
    public AsyncRelayCommand StartChatCommand { get; }
    public AsyncRelayCommand CreateNewChatCommand { get; }
    public AsyncRelayCommand EditCharacterCommand { get; }
    public AsyncRelayCommand OpenCharacterToolsCommand { get; }
    public AsyncRelayCommand ShowCharacterEditorCommand { get; }
    public AsyncRelayCommand ShowCharacterOverviewCommand { get; }
    public AsyncRelayCommand ToggleClassificationCommand { get; }
    public AsyncRelayCommand ReplaceCharacterAvatarCommand { get; }
    public AsyncRelayCommand DeleteCharacterCommand { get; }
    public AsyncRelayCommand OpenCharacterConversationCommand { get; }
    public AsyncRelayCommand CloseCharacterToolsCommand { get; }
    public AsyncRelayCommand CreateShelfCommand { get; }
    public AsyncRelayCommand RenameShelfCommand { get; }
    public AsyncRelayCommand DeleteShelfCommand { get; }
    public RelayCommand ToggleShelfBatchModeCommand { get; }
    public AsyncRelayCommand AddToShelfCommand { get; }
    public AsyncRelayCommand RemoveFromShelfCommand { get; }
    public AsyncRelayCommand RemoveSelectedFromShelfCommand { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                ApplyFilter();
            }
        }
    }

    public Character? ActiveCharacter => _characterSession?.Character;

    // Compatibility alias for command guards and existing tests. It is read-only:
    // the detail session has exactly one writable character reference.
    public Character? SelectedCharacter => ActiveCharacter;

    public CharacterShelfListItemViewModel? SelectedShelf
    {
        get => _selectedShelf;
        set
        {
            if (!SetProperty(ref _selectedShelf, value))
            {
                return;
            }

            IsShelfBatchMode = false;
            OnPropertyChanged(nameof(IsCustomShelfSelected));
            var version = ++_shelfSelectionVersion;
            _ = LoadSelectedShelfSafelyAsync(version);
            RaiseSelectionCanExecuteChanged();
        }
    }

    public CharacterShelfListItemViewModel? MembershipShelf
    {
        get => _membershipShelf;
        set
        {
            if (SetProperty(ref _membershipShelf, value))
            {
                AddToShelfCommand.RaiseCanExecuteChanged();
                RemoveFromShelfCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasUnsavedChanges => Editor.IsDirty;

    public bool IsCharacterToolsOpen => _characterSession is not null;

    public bool IsCharacterEditing =>
        _characterSession?.Mode == CharacterDetailMode.Edit;

    public bool IsClassificationOpen =>
        _characterSession?.Mode == CharacterDetailMode.Classification;

    public bool IsCustomShelfSelected =>
        SelectedShelf is { IsAllCharacters: false };

    public bool IsShelfBatchMode
    {
        get => _isShelfBatchMode;
        private set
        {
            if (SetProperty(ref _isShelfBatchMode, value))
            {
                RemoveSelectedFromShelfCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public int CharacterConversationCount => CharacterConversations.Count;

    public string ImportReportText
    {
        get => _importReportText;
        private set => SetProperty(ref _importReportText, value);
    }

    public CharacterCardScale Scale
    {
        get => _scale;
        private set
        {
            if (!SetProperty(ref _scale, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CardWidth));
            OnPropertyChanged(nameof(CardHeight));
            OnPropertyChanged(nameof(ScaleLabel));
        }
    }

    public double CardWidth => Scale switch
    {
        CharacterCardScale.Dense => 142,
        CharacterCardScale.Large => 260,
        _ => 190
    };

    public double CardHeight => Scale switch
    {
        CharacterCardScale.Dense => 250,
        CharacterCardScale.Large => 430,
        _ => 330
    };

    public string ScaleLabel => Scale switch
    {
        CharacterCardScale.Dense => "密集",
        CharacterCardScale.Large => "大图",
        _ => "中等"
    };

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public async Task LoadAsync()
    {
        var sessionAtStart = _characterSession;
        var storedScale = await _settings.GetAsync("characters.cardScale");
        if (Enum.TryParse<CharacterCardScale>(storedScale, out var scale))
        {
            Scale = scale;
        }

        var selectedShelfId = SelectedShelf?.Id ?? CharacterShelfListItemViewModel.All.Id;
        var membershipShelfId = MembershipShelf?.Id;
        var characters = await _repository.ListAsync();
        var shelves = await _shelves.ListAsync();

        Characters.Clear();
        foreach (var character in characters)
        {
            Characters.Add(character);
        }

        ShelfItems.Clear();
        CustomShelfItems.Clear();
        ShelfItems.Add(CharacterShelfListItemViewModel.All);
        foreach (var shelf in shelves)
        {
            var item = new CharacterShelfListItemViewModel(
                shelf.Id,
                shelf.Name,
                false,
                shelf);
            ShelfItems.Add(item);
            CustomShelfItems.Add(item);
        }

        _selectedShelf = ShelfItems.FirstOrDefault(item => item.Id == selectedShelfId)
                         ?? CharacterShelfListItemViewModel.All;
        IsShelfBatchMode = false;
        MembershipShelf = CustomShelfItems.FirstOrDefault(item =>
                              item.Id == membershipShelfId)
                          ?? CustomShelfItems.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedShelf));
        OnPropertyChanged(nameof(IsCustomShelfSelected));
        await LoadSelectedShelfAsync(++_shelfSelectionVersion);

        if (!ReferenceEquals(_characterSession, sessionAtStart)
            || sessionAtStart is null)
        {
            return;
        }

        var activeCharacter = Characters.FirstOrDefault(character =>
            character.Id == sessionAtStart.Character.Id);
        if (activeCharacter is not null && !sessionAtStart.Editor.IsDirty)
        {
            RebindCharacterSession(sessionAtStart, activeCharacter);
            await LoadCharacterConversationsAsync(sessionAtStart);
        }
        else if (activeCharacter is null)
        {
            if (EndCharacterSession(sessionAtStart))
            {
                Status = "原角色已不存在，已返回角色书架。";
            }
        }
    }

    public Task<bool> ConfirmCanLeaveAsync() =>
        ConfirmCanLeaveAsync(_characterSession);

    private async Task ImportAsync()
    {
        var sessionAtStart = _characterSession;
        if (!await ConfirmCanLeaveAsync(sessionAtStart)
            || !ReferenceEquals(_characterSession, sessionAtStart))
        {
            return;
        }

        var path = _fileDialog.PickCharacterCard();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var result = await _cardLibrary.ImportAsync(path);
            await LoadAsync();
            if (!ReferenceEquals(_characterSession, sessionAtStart))
            {
                Status = $"已导入：{result.Character.Name}；当前角色页面保持不变。";
                return;
            }

            var imported = Characters.First(character => character.Id == result.Character.Id);
            var session = BeginCharacterSession(imported, editImmediately: false);
            await LoadCharacterConversationsAsync(session);
            Status = result.Report.Warnings.Count == 0
                ? $"已导入：{result.Character.Name}；原文件未修改，工作副本已保存。"
                : $"已导入：{result.Character.Name}；报告包含 {result.Report.Warnings.Count} 条提示。";
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(_characterSession, sessionAtStart))
            {
                Status = $"导入失败：{exception.Message}";
            }
        }
    }

    private async Task ExportAsync()
    {
        var session = _characterSession;
        if (session is null
            || !await ConfirmCanLeaveAsync(session)
            || !IsCurrentCharacterSession(session))
        {
            return;
        }

        var current = session.Character;
        var path = _fileDialog.PickCharacterCardExportPath(current);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var result = await _cardLibrary.ExportAsync(current, path);
            if (IsCurrentCharacterSession(session))
            {
                Status = result.Warnings.Count == 0
                    ? $"已导出 {Path.GetFileName(result.DestinationPath)}；保留资源 {result.PreservedResourceCount} 项。"
                    : $"导出完成；{string.Join("；", result.Warnings)}";
            }
        }
        catch (Exception exception)
        {
            if (IsCurrentCharacterSession(session))
            {
                Status = $"导出失败：{exception.Message}";
            }
        }
    }

    private async Task SaveCharacterAsync()
    {
        _ = await SaveCharacterAsync(_characterSession);
    }

    private async Task<bool> SaveCharacterAsync(CharacterDetailSession? session)
    {
        if (session is null || !IsCurrentCharacterSession(session))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(session.Editor.Name))
        {
            Status = "角色名称不能为空。";
            return false;
        }

        if (!string.Equals(
                session.Editor.CharacterId,
                session.Character.Id,
                StringComparison.Ordinal))
        {
            Status = "角色编辑会话已失效，未执行保存。请返回书架后重新进入。";
            return false;
        }

        try
        {
            var savedCharacter = CloneCharacter(session.Character);
            var savedChangeVersion = session.Editor.ChangeVersion;
            session.Editor.ApplyTo(savedCharacter);
            savedCharacter.UpdatedAt = DateTimeOffset.Now;
            await _repository.UpsertAsync(savedCharacter);

            session.ReplaceCharacter(savedCharacter);
            session.Editor.MarkSaved(savedChangeVersion);
            if (IsCurrentCharacterSession(session))
            {
                NotifyCharacterSessionChanged();
                Status = $"已保存角色设定：{savedCharacter.Name}；现有聊天内容未改动。";
            }

            await RefreshCharactersPreservingEditorAsync(session);
            return true;
        }
        catch (Exception exception)
        {
            if (IsCurrentCharacterSession(session))
            {
                Status = $"角色设定未保存：{exception.Message}";
            }

            return false;
        }
    }

    private async Task EditRawCardJsonAsync()
    {
        var session = _characterSession;
        if (session is null)
        {
            return;
        }

        var edited = await _interaction.EditTextAsync(
            $"原始角色卡 JSON · {session.Editor.Name}",
            "编辑后会先解析和校验，再同步到当前角色编辑缓冲区；关闭窗口不会修改角色。",
            session.Editor.RawCardJson);
        if (edited is null || !IsCurrentCharacterSession(session))
        {
            return;
        }

        try
        {
            session.Editor.ReplaceRawJson(edited);
            Status = "原始 JSON 已通过解析并进入未保存缓冲区。";
        }
        catch (Exception exception)
        {
            Status = $"原始 JSON 未应用：{exception.Message}";
        }
    }

    private async Task StartChatAsync(object? parameter)
    {
        var session = _characterSession;
        if (parameter is Character character
            && await ConfirmCanLeaveAsync(session)
            && ReferenceEquals(_characterSession, session))
        {
            var currentCharacter = session is not null
                && string.Equals(
                    session.CharacterId,
                    character.Id,
                    StringComparison.Ordinal)
                    ? session.Character
                    : character;
            await _startChat(currentCharacter);
        }
    }

    private async Task CreateNewChatAsync(object? parameter)
    {
        var session = _characterSession;
        if (parameter is Character character
            && await ConfirmCanLeaveAsync(session)
            && ReferenceEquals(_characterSession, session))
        {
            var currentCharacter = session is not null
                && string.Equals(
                    session.CharacterId,
                    character.Id,
                    StringComparison.Ordinal)
                    ? session.Character
                    : character;
            await _createNewChat(currentCharacter);
        }
    }

    private Task OpenCharacterOverviewAsync(object? parameter) =>
        OpenCharacterAsync(parameter, editImmediately: false);

    private Task OpenCharacterEditorAsync(object? parameter) =>
        OpenCharacterAsync(parameter, editImmediately: true);

    private async Task OpenCharacterAsync(object? parameter, bool editImmediately)
    {
        if (parameter is not Character character)
        {
            return;
        }

        var previousSession = _characterSession;
        if (previousSession is not null
            && string.Equals(
                previousSession.Character.Id,
                character.Id,
                StringComparison.Ordinal))
        {
            if (editImmediately && !previousSession.HasValidEditorBinding)
            {
                Status = "角色编辑会话已失效，未打开空白编辑器。请返回书架后重新进入。";
                return;
            }

            SetCharacterDetailMode(editImmediately
                ? CharacterDetailMode.Edit
                : CharacterDetailMode.Overview);
            return;
        }

        if (!await ConfirmCanLeaveAsync(previousSession)
            || !ReferenceEquals(_characterSession, previousSession))
        {
            return;
        }

        var currentCharacter = Characters.FirstOrDefault(item => item.Id == character.Id)
                               ?? character;
        var session = BeginCharacterSession(
            currentCharacter,
            editImmediately);
        Status = editImmediately
            ? $"正在编辑：{currentCharacter.Name}"
            : $"已进入角色主页：{currentCharacter.Name}";
        try
        {
            await LoadCharacterConversationsAsync(session);
        }
        catch (Exception exception)
        {
            if (IsCurrentCharacterSession(session))
            {
                Status = $"角色主页已打开，但聊天列表读取失败：{exception.Message}";
            }
        }
    }

    private Task ShowCharacterEditorAsync()
    {
        var session = _characterSession;
        if (session is null)
        {
            return Task.CompletedTask;
        }

        if (!session.HasValidEditorBinding)
        {
            Status = "角色编辑会话已失效，未打开空白编辑器。请返回书架后重新进入。";
            return Task.CompletedTask;
        }

        SetCharacterDetailMode(CharacterDetailMode.Edit);
        Status = $"正在编辑：{session.Character.Name}";
        return Task.CompletedTask;
    }

    private async Task ShowCharacterOverviewAsync()
    {
        var session = _characterSession;
        if (session is null
            || !await ConfirmCanLeaveAsync(session)
            || !IsCurrentCharacterSession(session))
        {
            return;
        }

        SetCharacterDetailMode(CharacterDetailMode.Overview);
        Status = $"已返回 {session.Character.Name} 的角色主页。";
    }

    private async Task ToggleClassificationAsync()
    {
        var session = _characterSession;
        if (session is null)
        {
            return;
        }

        if (IsCharacterEditing
            && (!await ConfirmCanLeaveAsync(session)
                || !IsCurrentCharacterSession(session)))
        {
            return;
        }

        SetCharacterDetailMode(IsClassificationOpen
            ? CharacterDetailMode.Overview
            : CharacterDetailMode.Classification);
    }

    private async Task OpenCharacterConversationAsync(object? parameter)
    {
        var session = _characterSession;
        if (session is null
            || parameter is not CharacterConversationListItemViewModel item
            || !session.Conversations.Contains(item)
            || !await ConfirmCanLeaveAsync(session)
            || !IsCurrentCharacterSession(session))
        {
            return;
        }

        await _openConversation(item.Summary);
    }

    private async Task ReplaceCharacterAvatarAsync()
    {
        var session = _characterSession;
        if (session is null
            || !await ConfirmCanLeaveAsync(session)
            || !IsCurrentCharacterSession(session))
        {
            return;
        }

        var path = _fileDialog.PickCharacterAvatar();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            await _cardLibrary.ReplaceAvatarAsync(session.Character, path);
            await RefreshCharactersPreservingEditorAsync(session);
            if (IsCurrentCharacterSession(session))
            {
                Status = $"已替换 {session.Character.Name} 的本地展示图片；原始角色卡文件未修改。";
            }
        }
        catch (Exception exception)
        {
            if (IsCurrentCharacterSession(session))
            {
                Status = $"替换图片失败：{exception.Message}";
            }
        }
    }

    private async Task DeleteCharacterAsync()
    {
        var session = _characterSession;
        if (session is null
            || !await ConfirmCanLeaveAsync(session)
            || !IsCurrentCharacterSession(session))
        {
            return;
        }

        var character = session.Character;
        try
        {
            var conversations = await _conversations.ListByCharacterAsync(character.Id);
            if (!IsCurrentCharacterSession(session))
            {
                return;
            }

            if (!_interaction.ConfirmCharacterDeletion(character.Name, conversations.Count))
            {
                return;
            }

            await _repository.DeleteAsync(character.Id);
            var endedCurrentSession = EndCharacterSession(session);
            await LoadAsync();
            if (endedCurrentSession)
            {
                Status = $"已从角色书架删除 {character.Name}；聊天记录和导入工作副本仍保留。";
            }
        }
        catch (Exception exception)
        {
            if (IsCurrentCharacterSession(session)
                || _characterSession is null)
            {
                Status = $"删除角色卡失败：{exception.Message}";
            }
        }
    }

    private async Task CloseToolsAsync()
    {
        var session = _characterSession;
        if (session is null
            || !await ConfirmCanLeaveAsync(session)
            || !IsCurrentCharacterSession(session))
        {
            return;
        }

        if (EndCharacterSession(session))
        {
            Status = "已返回角色书架。";
        }
    }

    private async Task LoadCharacterConversationsAsync(
        CharacterDetailSession session)
    {
        var cancellationToken = session.StartRead();
        IReadOnlyList<ConversationSummary> conversations;
        try
        {
            conversations = await _conversations.ListByCharacterAsync(
                session.Character.Id,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (!IsCurrentCharacterSession(session))
        {
            return;
        }

        session.Conversations.Clear();
        foreach (var conversation in conversations)
        {
            session.Conversations.Add(
                new CharacterConversationListItemViewModel(conversation));
        }

        OnPropertyChanged(nameof(CharacterConversationCount));
    }

    private async Task<bool> ConfirmCanLeaveAsync(
        CharacterDetailSession? session)
    {
        if (session is null || !session.Editor.IsDirty)
        {
            return true;
        }

        switch (_interaction.ConfirmUnsavedCharacterChanges(session.Editor.Name))
        {
            case UnsavedChangesDecision.Cancel:
                return false;
            case UnsavedChangesDecision.Discard:
                if (!IsCurrentCharacterSession(session))
                {
                    return false;
                }

                session.Editor.Load(session.Character);
                return true;
            case UnsavedChangesDecision.Save:
                return await SaveCharacterAsync(session)
                       && IsCurrentCharacterSession(session)
                       && !session.Editor.IsDirty;
            default:
                return false;
        }
    }

    private async Task CreateShelfAsync()
    {
        var name = await _interaction.EditTextAsync(
            "新建书架",
            "输入自定义书架名称。角色卡可同时放入多个书架。",
            string.Empty);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var shelf = new CharacterShelf
        {
            Name = name.Trim(),
            SortIndex = ShelfItems.Count
        };
        try
        {
            await _shelves.UpsertAsync(shelf);
            await ReloadShelvesAsync(shelf.Id);
            Status = $"已新建书架：{shelf.Name}";
        }
        catch (Exception exception)
        {
            Status = $"新建书架失败：{exception.Message}";
        }
    }

    private async Task RenameShelfAsync()
    {
        if (SelectedShelf?.Shelf is not { } shelf)
        {
            return;
        }

        var name = await _interaction.EditTextAsync(
            "重命名书架",
            "修改书架名称。角色卡和聊天记录不会受影响。",
            shelf.Name);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        shelf.Name = name.Trim();
        shelf.UpdatedAt = DateTimeOffset.Now;
        try
        {
            await _shelves.UpsertAsync(shelf);
            await ReloadShelvesAsync(shelf.Id);
            Status = $"书架已重命名为：{shelf.Name}";
        }
        catch (Exception exception)
        {
            Status = $"重命名失败：{exception.Message}";
        }
    }

    private async Task DeleteShelfAsync()
    {
        if (SelectedShelf?.Shelf is not { } shelf
            || !_interaction.ConfirmShelfDeletion(shelf.Name))
        {
            return;
        }

        await _shelves.DeleteAsync(shelf.Id);
        await ReloadShelvesAsync(CharacterShelfListItemViewModel.All.Id);
        Status = $"已删除书架：{shelf.Name}；角色卡未删除。";
    }

    private async Task AddToShelfAsync()
    {
        var session = _characterSession;
        if (MembershipShelf?.Shelf is not { } shelf || session is null)
        {
            return;
        }

        var character = session.Character;
        await _shelves.AddCharacterAsync(shelf.Id, character.Id);
        await LoadSelectedShelfAsync(++_shelfSelectionVersion);
        if (IsCurrentCharacterSession(session))
        {
            Status = $"已把 {character.Name} 加入书架“{shelf.Name}”。";
        }
    }

    private async Task RemoveFromShelfAsync()
    {
        var session = _characterSession;
        if (MembershipShelf?.Shelf is not { } shelf || session is null)
        {
            return;
        }

        var character = session.Character;
        await _shelves.RemoveCharacterAsync(shelf.Id, character.Id);
        await LoadSelectedShelfAsync(++_shelfSelectionVersion);
        if (IsCurrentCharacterSession(session))
        {
            Status = $"已从书架“{shelf.Name}”移除 {character.Name}；角色卡未删除。";
        }
    }

    private void ToggleShelfBatchMode(object? parameter)
    {
        if (!IsCustomShelfSelected)
        {
            return;
        }

        var next = !IsShelfBatchMode;
        if (parameter is IList selection
            && !selection.IsReadOnly
            && !selection.IsFixedSize)
        {
            selection.Clear();
        }

        IsShelfBatchMode = next;
        Status = next
            ? $"批量整理“{SelectedShelf!.Name}”：勾选角色后可从本书架移出。"
            : $"已退出“{SelectedShelf!.Name}”的批量整理。";
    }

    private async Task RemoveSelectedFromShelfAsync(object? parameter)
    {
        var selectedShelf = SelectedShelf;
        if (!IsShelfBatchMode
            || selectedShelf?.Shelf is not { } shelf
            || parameter is not IEnumerable selection)
        {
            return;
        }

        var characterIds = selection
            .OfType<Character>()
            .Select(character => character.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (characterIds.Length == 0)
        {
            Status = $"请先勾选要从书架“{shelf.Name}”移出的角色。";
            return;
        }

        var removedCount = 0;
        try
        {
            foreach (var characterId in characterIds)
            {
                await _shelves.RemoveCharacterAsync(shelf.Id, characterId);
                removedCount++;
            }

            if (!string.Equals(SelectedShelf?.Id, shelf.Id, StringComparison.Ordinal))
            {
                return;
            }

            var version = ++_shelfSelectionVersion;
            await LoadSelectedShelfAsync(version);
            if (version == _shelfSelectionVersion
                && string.Equals(SelectedShelf?.Id, shelf.Id, StringComparison.Ordinal))
            {
                Status =
                    $"已从书架“{shelf.Name}”移出 {removedCount} 个角色；角色卡和聊天记录未删除。";
            }
        }
        catch (Exception exception)
        {
            if (string.Equals(SelectedShelf?.Id, shelf.Id, StringComparison.Ordinal))
            {
                Status =
                    $"批量移出未全部完成（已完成 {removedCount}/{characterIds.Length}）：{exception.Message}";
                await LoadSelectedShelfAsync(++_shelfSelectionVersion);
            }
        }
    }

    private async Task ReloadShelvesAsync(string selectedShelfId)
    {
        ShelfItems.Clear();
        CustomShelfItems.Clear();
        ShelfItems.Add(CharacterShelfListItemViewModel.All);
        foreach (var shelf in await _shelves.ListAsync())
        {
            var item = new CharacterShelfListItemViewModel(
                shelf.Id,
                shelf.Name,
                false,
                shelf);
            ShelfItems.Add(item);
            CustomShelfItems.Add(item);
        }

        _selectedShelf = ShelfItems.FirstOrDefault(item => item.Id == selectedShelfId)
                         ?? CharacterShelfListItemViewModel.All;
        IsShelfBatchMode = false;
        MembershipShelf = CustomShelfItems.FirstOrDefault(item =>
                              item.Id == MembershipShelf?.Id)
                          ?? CustomShelfItems.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedShelf));
        OnPropertyChanged(nameof(IsCustomShelfSelected));
        await LoadSelectedShelfAsync(++_shelfSelectionVersion);
    }

    private async Task LoadSelectedShelfAsync(int version)
    {
        var shelf = SelectedShelf;
        var characterIds = shelf is { IsAllCharacters: false }
            ? await _shelves.ListCharacterIdsAsync(shelf.Id)
            : new HashSet<string>(
                Characters.Select(character => character.Id),
                StringComparer.Ordinal);
        if (version != _shelfSelectionVersion)
        {
            return;
        }

        _selectedShelfCharacterIds = characterIds;
        if (shelf is { IsAllCharacters: false })
        {
            MembershipShelf = CustomShelfItems.FirstOrDefault(item => item.Id == shelf.Id)
                              ?? MembershipShelf;
        }

        ApplyFilter();
    }

    private async Task LoadSelectedShelfSafelyAsync(int version)
    {
        try
        {
            await LoadSelectedShelfAsync(version);
        }
        catch (Exception exception) when (version == _shelfSelectionVersion)
        {
            Status = $"书架读取失败：{exception.Message}";
        }
    }

    private void ApplyFilter()
    {
        VisibleCharacters.Clear();
        var query = SearchText.Trim();
        foreach (var character in Characters.Where(character =>
                     _selectedShelfCharacterIds.Contains(character.Id)
                     && (query.Length == 0
                         || character.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                         || character.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
                         || character.Personality.Contains(query, StringComparison.OrdinalIgnoreCase))))
        {
            VisibleCharacters.Add(character);
        }
    }

    private async Task RefreshCharactersPreservingEditorAsync(
        CharacterDetailSession session)
    {
        var characters = await _repository.ListAsync();
        if (!IsCurrentCharacterSession(session))
        {
            return;
        }

        Characters.Clear();
        foreach (var character in characters)
        {
            Characters.Add(string.Equals(
                    character.Id,
                    session.Character.Id,
                    StringComparison.Ordinal)
                ? session.Character
                : character);
        }

        await LoadSelectedShelfAsync(++_shelfSelectionVersion);
        if (IsCurrentCharacterSession(session))
        {
            NotifyCharacterSessionChanged();
        }
    }

    private async Task SetScaleAsync(CharacterCardScale scale)
    {
        Scale = scale;
        await _settings.SetAsync("characters.cardScale", scale.ToString());
    }

    private CharacterDetailSession BeginCharacterSession(
        Character character,
        bool editImmediately)
    {
        var session = new CharacterDetailSession(
            character,
            editImmediately
                ? CharacterDetailMode.Edit
                : CharacterDetailMode.Overview);
        SetCharacterSession(session);
        return session;
    }

    private void RebindCharacterSession(
        CharacterDetailSession session,
        Character character)
    {
        if (!IsCurrentCharacterSession(session))
        {
            return;
        }

        session.Rebind(character);
        NotifyCharacterSessionChanged();
    }

    private bool IsCurrentCharacterSession(CharacterDetailSession session) =>
        ReferenceEquals(_characterSession, session)
        && _characterSession.SessionId == session.SessionId
        && string.Equals(
            _characterSession.Character.Id,
            session.CharacterId,
            StringComparison.Ordinal);

    private bool EndCharacterSession(CharacterDetailSession session)
    {
        if (!IsCurrentCharacterSession(session))
        {
            return false;
        }

        SetCharacterSession(null);
        return true;
    }

    private void SetCharacterSession(CharacterDetailSession? session)
    {
        var previousEditor = Editor;
        previousEditor.PropertyChanged -= OnEditorPropertyChanged;
        if (!ReferenceEquals(_characterSession, session))
        {
            _characterSession?.CancelPendingReads();
        }

        _characterSession = session;
        Editor.PropertyChanged += OnEditorPropertyChanged;
        NotifyCharacterSessionChanged();
        OnPropertyChanged(nameof(IsCharacterToolsOpen));
        OnPropertyChanged(nameof(IsCharacterEditing));
        OnPropertyChanged(nameof(IsClassificationOpen));
    }

    private void SetCharacterDetailMode(CharacterDetailMode mode)
    {
        if (_characterSession is null
            || !_characterSession.SetMode(mode))
        {
            return;
        }

        OnPropertyChanged(nameof(IsCharacterEditing));
        OnPropertyChanged(nameof(IsClassificationOpen));
    }

    private void NotifyCharacterSessionChanged()
    {
        OnPropertyChanged(nameof(ActiveCharacter));
        OnPropertyChanged(nameof(SelectedCharacter));
        OnPropertyChanged(nameof(Editor));
        OnPropertyChanged(nameof(CharacterConversations));
        OnPropertyChanged(nameof(CharacterConversationCount));
        OnPropertyChanged(nameof(HasUnsavedChanges));
        UpdateImportReport();
        RaiseSelectionCanExecuteChanged();
    }

    private void OnEditorPropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(CharacterEditBuffer.IsDirty))
        {
            return;
        }

        OnPropertyChanged(nameof(HasUnsavedChanges));
        SaveCharacterCommand.RaiseCanExecuteChanged();
    }

    private static Character CloneCharacter(Character source) =>
        new()
        {
            Id = source.Id,
            Name = source.Name,
            Description = source.Description,
            Personality = source.Personality,
            Scenario = source.Scenario,
            FirstMessage = source.FirstMessage,
            AvatarPath = source.AvatarPath,
            RawCardJson = source.RawCardJson,
            SourceCardFormat = source.SourceCardFormat,
            SourceCardPath = source.SourceCardPath,
            ImportReportJson = source.ImportReportJson,
            CreatedAt = source.CreatedAt,
            UpdatedAt = source.UpdatedAt
        };

    private void UpdateImportReport()
    {
        if (SelectedCharacter is null
            || string.IsNullOrWhiteSpace(SelectedCharacter.ImportReportJson))
        {
            ImportReportText = "选择角色后可查看导入报告。";
            return;
        }

        try
        {
            var report = JsonSerializer.Deserialize<CharacterCardImportReport>(
                SelectedCharacter.ImportReportJson);
            if (report is null
                || string.IsNullOrWhiteSpace(report.FormatName)
                || report.UnknownFieldPaths is null
                || report.Resources is null
                || report.Warnings is null)
            {
                ImportReportText = "此角色没有可读取的导入报告。";
                return;
            }

            var text = new StringBuilder()
                .AppendLine($"格式：{report.FormatName}")
                .AppendLine($"规格：{report.Spec} {report.SpecVersion}".TrimEnd())
                .AppendLine($"源文件：{report.SourceFileName}")
                .AppendLine($"原始工作副本：{(report.SourcePreserved ? "已保存" : "未保存")}")
                .AppendLine($"未知字段：{report.UnknownFieldPaths.Count} 项")
                .AppendLine($"内嵌资源：{report.Resources.Count} 项");
            foreach (var resource in report.Resources)
            {
                text.AppendLine(
                    $"  • {resource.RelativePath} · {resource.Size} B · SHA-256 {resource.Sha256[..Math.Min(12, resource.Sha256.Length)]}…");
            }

            if (report.Warnings.Count > 0)
            {
                text.AppendLine("提示：");
                foreach (var warning in report.Warnings)
                {
                    text.AppendLine($"  • {warning}");
                }
            }

            ImportReportText = text.ToString().TrimEnd();
        }
        catch (JsonException)
        {
            ImportReportText = "导入报告 JSON 已损坏或来自不兼容版本。";
        }
    }

    private void RaiseSelectionCanExecuteChanged()
    {
        ExportCommand.RaiseCanExecuteChanged();
        SaveCharacterCommand.RaiseCanExecuteChanged();
        EditRawCardJsonCommand.RaiseCanExecuteChanged();
        ReplaceCharacterAvatarCommand.RaiseCanExecuteChanged();
        DeleteCharacterCommand.RaiseCanExecuteChanged();
        RenameShelfCommand.RaiseCanExecuteChanged();
        DeleteShelfCommand.RaiseCanExecuteChanged();
        ToggleShelfBatchModeCommand.RaiseCanExecuteChanged();
        AddToShelfCommand.RaiseCanExecuteChanged();
        RemoveFromShelfCommand.RaiseCanExecuteChanged();
        RemoveSelectedFromShelfCommand.RaiseCanExecuteChanged();
    }

    private enum CharacterDetailMode
    {
        Overview,
        Edit,
        Classification
    }

    private sealed class CharacterDetailSession
    {
        private readonly object _readSync = new();
        private CancellationTokenSource _readCancellation = new();

        public CharacterDetailSession(
            Character character,
            CharacterDetailMode mode)
        {
            Character = character;
            CharacterId = character.Id;
            Mode = mode;
            Editor.Load(character);
        }

        public Guid SessionId { get; } = Guid.NewGuid();
        public string CharacterId { get; }
        public Character Character { get; private set; }
        public CharacterDetailMode Mode { get; private set; }
        public CharacterEditBuffer Editor { get; } = new();
        public ObservableCollection<CharacterConversationListItemViewModel>
            Conversations { get; } = [];

        public bool HasValidEditorBinding =>
            string.Equals(
                CharacterId,
                Character.Id,
                StringComparison.Ordinal)
            && string.Equals(
                CharacterId,
                Editor.CharacterId,
                StringComparison.Ordinal);

        public CancellationToken StartRead()
        {
            lock (_readSync)
            {
                _readCancellation.Cancel();
                _readCancellation.Dispose();
                _readCancellation = new CancellationTokenSource();
                return _readCancellation.Token;
            }
        }

        public void CancelPendingReads()
        {
            lock (_readSync)
            {
                _readCancellation.Cancel();
            }
        }

        public bool SetMode(CharacterDetailMode mode)
        {
            if (Mode == mode)
            {
                return false;
            }

            Mode = mode;
            return true;
        }

        public void Rebind(Character character)
        {
            if (!string.Equals(
                    CharacterId,
                    character.Id,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "不能把详情会话重新绑定到另一个角色。");
            }

            Character = character;
            Editor.Load(character);
        }

        public void ReplaceCharacter(Character character)
        {
            if (!string.Equals(
                    CharacterId,
                    character.Id,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "不能用另一个角色替换当前详情会话。");
            }

            Character = character;
        }
    }
}
