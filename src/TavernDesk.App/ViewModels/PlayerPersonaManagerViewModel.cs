using System.Collections.ObjectModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using TavernDesk.App.Presentation;
using TavernDesk.App.Services;
using TavernDesk.Core.Abstractions;

namespace TavernDesk.App.ViewModels;

public sealed class PlayerPersonaProfileViewModel : ViewModelBase
{
    private string _name;
    private string _description;

    public PlayerPersonaProfileViewModel(
        string id,
        string name,
        string description)
    {
        Id = id;
        _name = name;
        _description = description;
    }

    public string Id { get; }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }
}

/// <summary>
/// Application-scoped player persona catalog shared by Settings and Chat.
/// The editor is a buffer: only Save writes a profile; Cancel restores it.
/// </summary>
public sealed class PlayerPersonaManagerViewModel : ViewModelBase
{
    public const string StorageKey = "persona.profiles.v1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    private static readonly HashSet<string> ReservedNames = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "null",
        "undefined",
        "nan",
        "infinity",
        "+infinity",
        "-infinity",
        "true",
        "false",
        "none",
        "empty",
        "object",
        "array",
        "string",
        "number",
        "boolean",
        "constructor",
        "prototype",
        "__proto__"
    };

    private readonly IAppSettingsRepository _settings;
    private readonly IUserInteractionService? _interaction;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly SemaphoreSlim _persistGate = new(1, 1);
    private PlayerPersonaProfileViewModel? _selectedProfile;
    private string _editorName = string.Empty;
    private string _editorDescription = string.Empty;
    private string _status = "玩家人设保存在本机个人资料中。";
    private bool _loaded;
    private bool _suppressSelectionPersistence;
    private bool _isNewProfile;
    private PlayerPersonaProfileViewModel? _profileBeforeNew;
    private long _persistRevision;

    public PlayerPersonaManagerViewModel(
        IAppSettingsRepository settings,
        IUserInteractionService? interaction = null)
    {
        _settings = settings;
        _interaction = interaction;
        SaveCommand = new AsyncRelayCommand(SaveCurrentAsync, CanSave);
        CancelCommand = new RelayCommand(CancelEdits);
        NewCommand = new AsyncRelayCommand(CreateAsync);
        DeleteCommand = new AsyncRelayCommand(DeleteAsync, CanDelete);
    }

    public ObservableCollection<PlayerPersonaProfileViewModel> Profiles { get; } = [];

    public PlayerPersonaProfileViewModel? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (ReferenceEquals(_selectedProfile, value))
            {
                return;
            }

            if (_isNewProfile || HasUnsavedChanges)
            {
                CancelEdits();
            }

            if (!SetProperty(ref _selectedProfile, value))
            {
                return;
            }

            LoadEditorFromSelected();
            OnPropertyChanged(nameof(ActiveName));
            OnPropertyChanged(nameof(ActiveDescription));
            RaiseCommandState();

            if (_loaded && !_suppressSelectionPersistence && value is not null)
            {
                _ = PersistSelectionAsync();
            }
        }
    }

    public string EditorName
    {
        get => _editorName;
        set
        {
            if (SetProperty(ref _editorName, value))
            {
                OnPropertyChanged(nameof(HasUnsavedChanges));
                RaiseCommandState();
            }
        }
    }

    public string EditorDescription
    {
        get => _editorDescription;
        set
        {
            if (SetProperty(ref _editorDescription, value))
            {
                OnPropertyChanged(nameof(HasUnsavedChanges));
                RaiseCommandState();
            }
        }
    }

    public string ActiveName => _selectedProfile?.Name ?? "用户";

    public string ActiveDescription => _selectedProfile?.Description ?? string.Empty;

    public bool HasUnsavedChanges =>
        _selectedProfile is not null
        && (!string.Equals(
                EditorName,
                _selectedProfile.Name,
                StringComparison.Ordinal)
            || !string.Equals(
                EditorDescription,
                _selectedProfile.Description,
                StringComparison.Ordinal));

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public AsyncRelayCommand SaveCommand { get; }

    public RelayCommand CancelCommand { get; }

    public AsyncRelayCommand NewCommand { get; }

    public AsyncRelayCommand DeleteCommand { get; }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _loadGate.WaitAsync(cancellationToken);
        try
        {
            if (_loaded)
            {
                return;
            }

            var json = await _settings.GetAsync(StorageKey, cancellationToken);
            var migrated = false;
            string? migratedUnsafeName = null;
            var document = Deserialize(json);
            document.Profiles ??= [];
            if (document.Profiles.Count == 0)
            {
                var legacyName = await _settings.GetAsync(
                    "persona.name",
                    cancellationToken);
                var legacyDescription = await _settings.GetAsync(
                    "persona.description",
                    cancellationToken);
                document.Profiles.Add(new StoredProfile
                {
                    Id = "default",
                    Name = NormalizeName(legacyName),
                    Description = legacyDescription ?? string.Empty
                });
                document.SelectedProfileId = "default";
                migrated = true;
            }

            foreach (var rawProfile in document.Profiles)
            {
                var rawId = rawProfile?.Id;
                var rawName = rawProfile?.Name;
                var rawDescription = rawProfile?.Description;
                if (!string.IsNullOrWhiteSpace(rawName)
                    && IsUnsafeName(rawName.Trim()))
                {
                    migratedUnsafeName ??= rawName.Trim();
                }
                var profile = NormalizeProfile(rawProfile);
                if (profile is null)
                {
                    migrated = true;
                    continue;
                }

                migrated |= !string.Equals(
                                rawId,
                                profile.Id,
                                StringComparison.Ordinal)
                            || !string.Equals(
                                rawName,
                                profile.Name,
                                StringComparison.Ordinal)
                            || !string.Equals(
                                rawDescription,
                                profile.Description,
                                StringComparison.Ordinal);
                Profiles.Add(new PlayerPersonaProfileViewModel(
                    profile.Id,
                    profile.Name,
                    profile.Description));
            }

            _loaded = true;
            _suppressSelectionPersistence = true;
            try
            {
                SelectedProfile = Profiles.FirstOrDefault(profile =>
                    string.Equals(
                        profile.Id,
                        document.SelectedProfileId,
                        StringComparison.Ordinal))
                    ?? Profiles.First();
            }
            finally
            {
                _suppressSelectionPersistence = false;
            }

            if (migrated || string.IsNullOrWhiteSpace(json))
            {
                await PersistAsync(cancellationToken);
            }

            if (migratedUnsafeName is not null)
            {
                ShowValidationWarning(
                    "已修正不安全的玩家人设名称",
                    $"检测到旧资料中的名称“{migratedUnsafeName}”可能与程序保留值或内部对象字段冲突。\n\n"
                    + "该名称已自动改为“用户”，并已清理保存。为避免出现 BUG，后续请改用普通名称，例如“玩家1”“冒险者”或你自己的普通昵称。");
            }

            Status = Profiles.Count == 1
                ? "已载入 1 个玩家人设。"
                : $"已载入 {Profiles.Count} 个玩家人设。";
        }
        finally
        {
            _loadGate.Release();
        }
    }

    public async Task SaveCurrentAsync()
    {
        await LoadAsync();
        if (_selectedProfile is null)
        {
            Status = "请先选择一个玩家人设。";
            ShowValidationWarning(
                "无法保存玩家人设",
                "请先选择一个玩家人设后再保存。\n\n当前内容未保存。");
            return;
        }

        var name = EditorName.Trim();
        if (name.Length == 0)
        {
            Status = "玩家人设名称不能为空。";
            ShowValidationWarning(
                "玩家人设名称不能为空",
                "请填写一个普通的玩家人设名称后再保存。\n\n"
                + "建议使用“玩家1”“冒险者”或你自己的普通昵称。\n"
                + "当前内容未保存。");
            return;
        }

        if (name.Length > 80)
        {
            Status = "玩家人设名称不能超过 80 个字符。";
            ShowValidationWarning(
                "玩家人设名称过长",
                "名称不能超过 80 个字符。为避免显示或存储异常，建议缩短为普通名称。\n\n"
                + "当前内容未保存。");
            return;
        }

        if (ContainsControlCharacter(name))
        {
            Status = "玩家人设名称不能包含换行、制表符或其他控制字符。";
            ShowValidationWarning(
                "玩家人设名称不安全",
                "名称包含换行、制表符或其他控制字符。为避免出现 BUG，建议只使用普通中英文、数字和空格。\n\n"
                + "当前内容未保存。");
            return;
        }

        if (IsReservedName(name))
        {
            Status = "玩家人设名称不能使用程序保留值，例如 null、undefined、NaN、__proto__、constructor 或 prototype。";
            ShowValidationWarning(
                "玩家人设名称不安全",
                $"“{name}”可能与程序保留值或内部对象字段冲突。为避免出现 BUG，请更换为更安全的普通名称。\n\n"
                + "建议使用“玩家1”“冒险者”或你自己的普通昵称；不要使用 null、undefined、NaN、constructor、prototype 或 __proto__。\n"
                + "当前内容未保存。");
            return;
        }

        _selectedProfile.Name = name;
        _selectedProfile.Description = EditorDescription;
        _isNewProfile = false;
        _profileBeforeNew = null;
        EditorName = name;
        EditorDescription = _selectedProfile.Description;
        await PersistAsync();
        OnPropertyChanged(nameof(ActiveName));
        OnPropertyChanged(nameof(ActiveDescription));
        Status = $"已保存玩家人设“{name}”；后续新请求将使用当前选择。";
        RaiseCommandState();
    }

    private bool CanSave() => _selectedProfile is not null && HasUnsavedChanges;

    private void ShowValidationWarning(string title, string message) =>
        _interaction?.ShowWarning(title, message);

    public void CancelEdits()
    {
        if (_selectedProfile is null)
        {
            return;
        }

        if (_isNewProfile)
        {
            var draft = _selectedProfile;
            var previous = _profileBeforeNew;
            _isNewProfile = false;
            _profileBeforeNew = null;
            Profiles.Remove(draft);
            _suppressSelectionPersistence = true;
            try
            {
                SelectedProfile = previous is not null && Profiles.Contains(previous)
                    ? previous
                    : Profiles.FirstOrDefault();
            }
            finally
            {
                _suppressSelectionPersistence = false;
            }

            Status = "已取消新增，空白玩家人设未保存。";
            RaiseCommandState();
            return;
        }

        LoadEditorFromSelected();
        Status = $"已取消编辑，恢复玩家人设“{_selectedProfile.Name}”的已保存内容。";
        RaiseCommandState();
    }

    private async Task CreateAsync()
    {
        await LoadAsync();
        if (_isNewProfile || HasUnsavedChanges)
        {
            CancelEdits();
        }

        var previous = _selectedProfile;
        var profile = new PlayerPersonaProfileViewModel(
            Guid.NewGuid().ToString("N"),
            string.Empty,
            string.Empty);
        Profiles.Add(profile);
        _suppressSelectionPersistence = true;
        try
        {
            SelectedProfile = profile;
        }
        finally
        {
            _suppressSelectionPersistence = false;
        }

        _profileBeforeNew = previous;
        _isNewProfile = true;
        EditorName = string.Empty;
        EditorDescription = string.Empty;
        Status = "已新增空白人设草稿；请输入名称后点击保存，空白名称不会保存。";
        RaiseCommandState();
    }

    private bool CanDelete() =>
        SelectedProfile is not null
        && Profiles.Count > 1
        && !_isNewProfile;

    private async Task DeleteAsync()
    {
        await LoadAsync();
        if (!CanDelete() || SelectedProfile is null)
        {
            Status = "至少保留一个玩家人设。";
            return;
        }

        var deletedName = SelectedProfile.Name;
        var index = Profiles.IndexOf(SelectedProfile);
        _isNewProfile = false;
        _profileBeforeNew = null;
        Profiles.Remove(SelectedProfile);
        _suppressSelectionPersistence = true;
        try
        {
            SelectedProfile = Profiles[Math.Min(index, Profiles.Count - 1)];
        }
        finally
        {
            _suppressSelectionPersistence = false;
        }

        await PersistAsync();
        Status = $"已删除玩家人设“{deletedName}”。";
        RaiseCommandState();
    }

    private void LoadEditorFromSelected()
    {
        EditorName = _selectedProfile?.Name ?? "用户";
        EditorDescription = _selectedProfile?.Description ?? string.Empty;
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    private async Task PersistAsync(CancellationToken cancellationToken = default)
    {
        var document = new PersonaDocument
        {
            SelectedProfileId = _selectedProfile?.Id,
            Profiles = Profiles
                .Select(profile => new StoredProfile
                {
                    Id = profile.Id,
                    Name = profile.Name,
                    Description = profile.Description
                })
                .ToList()
        };
        var json = JsonSerializer.Serialize(document, JsonOptions);
        var activeName = _selectedProfile?.Name ?? "用户";
        var activeDescription = _selectedProfile?.Description ?? string.Empty;
        var revision = Interlocked.Increment(ref _persistRevision);
        await _persistGate.WaitAsync(cancellationToken);
        try
        {
            if (revision != Volatile.Read(ref _persistRevision))
            {
                return;
            }

            await _settings.SetAsync(StorageKey, json, cancellationToken);

            // Keep the old single-persona keys synchronized for older builds
            // and for local imports that still read those keys. The gate keeps
            // all three keys in the same selection order.
            await _settings.SetAsync(
                "persona.name",
                activeName,
                cancellationToken);
            await _settings.SetAsync(
                "persona.description",
                activeDescription,
                cancellationToken);
        }
        finally
        {
            _persistGate.Release();
        }
    }

    private async Task PersistSelectionAsync()
    {
        try
        {
            await PersistAsync();
        }
        catch (Exception exception)
        {
            Status = $"当前玩家人设选择未保存：{exception.Message}";
        }
    }

    private static PersonaDocument Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new PersonaDocument();
        }

        try
        {
            return JsonSerializer.Deserialize<PersonaDocument>(json, JsonOptions)
                   ?? new PersonaDocument();
        }
        catch (JsonException)
        {
            return new PersonaDocument();
        }
    }

    private static StoredProfile? NormalizeProfile(StoredProfile? profile)
    {
        if (profile is null)
        {
            return null;
        }

        profile.Id = string.IsNullOrWhiteSpace(profile.Id)
            ? Guid.NewGuid().ToString("N")
            : profile.Id.Trim();
        profile.Name = NormalizeName(profile.Name);
        profile.Description ??= string.Empty;
        return profile;
    }

    private static string NormalizeName(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? "用户"
            : IsUnsafeName(name.Trim())
                ? "用户"
                : name.Trim();

    private static bool IsReservedName(string name) =>
        ReservedNames.Contains(name)
        || name.Contains("__proto__", StringComparison.OrdinalIgnoreCase)
        || name.Contains(
            "constructor.prototype",
            StringComparison.OrdinalIgnoreCase);

    private static bool ContainsControlCharacter(string name) =>
        name.Any(char.IsControl);

    private static bool IsUnsafeName(string name) =>
        name.Length > 80
        || ContainsControlCharacter(name)
        || IsReservedName(name);

    private void RaiseCommandState()
    {
        SaveCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    private sealed class PersonaDocument
    {
        public string Schema { get; set; } = "taverndesk.persona-profiles.v1";

        public string? SelectedProfileId { get; set; }

        public List<StoredProfile> Profiles { get; set; } = [];
    }

    private sealed class StoredProfile
    {
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;
    }
}
