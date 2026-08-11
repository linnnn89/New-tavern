using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using TavernDesk.App.Localization;
using TavernDesk.App.Presentation;
using TavernDesk.App.Services;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;

namespace TavernDesk.App.ViewModels;

public sealed class PresetViewModel : ViewModelBase
{
    private readonly IPresetRepository _repository;
    private readonly IPresetResolver _resolver;
    private readonly IUserInteractionService _interaction;
    private readonly Action _contextChanged;
    private PromptPreset? _selectedPreset;
    private string _conversationId = string.Empty;
    private string? _characterId;
    private bool _globalMounted;
    private bool _characterMounted;
    private bool _conversationMounted;
    private int _globalSortIndex;
    private int _characterSortIndex;
    private int _conversationSortIndex;
    private string _effectiveOverlayJson = "{}";
    private string _effectiveSystemPrompt = string.Empty;
    private string _diagnosticsText = LanguageRuntime.GetString("Preset.NoneMounted");
    private string _status = LanguageRuntime.GetString("Preset.SelectConversation");
    private long _selectionVersion;

    public PresetViewModel(
        IPresetRepository repository,
        IPresetResolver resolver,
        IUserInteractionService interaction,
        Action contextChanged)
    {
        _repository = repository;
        _resolver = resolver;
        _interaction = interaction;
        _contextChanged = contextChanged;
        NewPresetCommand = new AsyncRelayCommand(NewPresetAsync);
        EditOverlayCommand = new AsyncRelayCommand(
            EditOverlayAsync,
            () => SelectedPreset is not null);
        RenameCommand = new AsyncRelayCommand(
            RenameAsync,
            () => SelectedPreset is not null);
        EditDescriptionCommand = new AsyncRelayCommand(
            EditDescriptionAsync,
            () => SelectedPreset is not null);
        DeleteCommand = new AsyncRelayCommand(
            DeleteAsync,
            () => SelectedPreset is not null);
        ToggleGlobalMountCommand = new AsyncRelayCommand(
            () => ToggleMountAsync(PresetScopeKind.Global),
            () => SelectedPreset is not null && IsConversationAvailable);
        ToggleCharacterMountCommand = new AsyncRelayCommand(
            () => ToggleMountAsync(PresetScopeKind.Character),
            () => SelectedPreset is not null && IsCharacterScopeAvailable);
        ToggleConversationMountCommand = new AsyncRelayCommand(
            () => ToggleMountAsync(PresetScopeKind.Conversation),
            () => SelectedPreset is not null && IsConversationAvailable);
        ApplyMountOrderCommand = new AsyncRelayCommand(
            ApplyMountOrderAsync,
            () => SelectedPreset is not null && IsConversationAvailable);
    }

    public ObservableCollection<PromptPreset> Presets { get; } = [];
    public AsyncRelayCommand NewPresetCommand { get; }
    public AsyncRelayCommand EditOverlayCommand { get; }
    public AsyncRelayCommand RenameCommand { get; }
    public AsyncRelayCommand EditDescriptionCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }
    public AsyncRelayCommand ToggleGlobalMountCommand { get; }
    public AsyncRelayCommand ToggleCharacterMountCommand { get; }
    public AsyncRelayCommand ToggleConversationMountCommand { get; }
    public AsyncRelayCommand ApplyMountOrderCommand { get; }

    public PromptPreset? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (!SetProperty(ref _selectedPreset, value))
            {
                return;
            }

            RaiseCommandStates();
            _ = LoadMountStateAsync(value, ++_selectionVersion);
        }
    }

    public bool IsConversationAvailable => _conversationId.Length > 0;
    public bool IsCharacterScopeAvailable => _characterId is { Length: > 0 };

    public bool GlobalMounted
    {
        get => _globalMounted;
        private set
        {
            if (SetProperty(ref _globalMounted, value))
            {
                OnPropertyChanged(nameof(GlobalMountLabel));
            }
        }
    }

    public bool CharacterMounted
    {
        get => _characterMounted;
        private set
        {
            if (SetProperty(ref _characterMounted, value))
            {
                OnPropertyChanged(nameof(CharacterMountLabel));
            }
        }
    }

    public bool ConversationMounted
    {
        get => _conversationMounted;
        private set
        {
            if (SetProperty(ref _conversationMounted, value))
            {
                OnPropertyChanged(nameof(ConversationMountLabel));
            }
        }
    }

    public int GlobalSortIndex
    {
        get => _globalSortIndex;
        set => SetProperty(ref _globalSortIndex, value);
    }

    public int CharacterSortIndex
    {
        get => _characterSortIndex;
        set => SetProperty(ref _characterSortIndex, value);
    }

    public int ConversationSortIndex
    {
        get => _conversationSortIndex;
        set => SetProperty(ref _conversationSortIndex, value);
    }

    public string GlobalMountLabel => GlobalMounted
        ? LanguageRuntime.GetString("Preset.Unmount.Global")
        : LanguageRuntime.GetString("Preset.Mount.Global");
    public string CharacterMountLabel => CharacterMounted
        ? LanguageRuntime.GetString("Preset.Unmount.Character")
        : LanguageRuntime.GetString("Preset.Mount.Character");
    public string ConversationMountLabel =>
        ConversationMounted
            ? LanguageRuntime.GetString("Preset.Unmount.Conversation")
            : LanguageRuntime.GetString("Preset.Mount.Conversation");

    public string EffectiveOverlayJson
    {
        get => _effectiveOverlayJson;
        private set => SetProperty(ref _effectiveOverlayJson, value);
    }

    public string DiagnosticsText
    {
        get => _diagnosticsText;
        private set => SetProperty(ref _diagnosticsText, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public async Task LoadAsync(
        Conversation conversation,
        CancellationToken cancellationToken = default)
    {
        _conversationId = conversation.Id;
        _characterId = conversation.CharacterId;
        OnPropertyChanged(nameof(IsConversationAvailable));
        OnPropertyChanged(nameof(IsCharacterScopeAvailable));
        RaiseCommandStates();
        await ReloadPresetsAsync(SelectedPreset?.Id, cancellationToken);
        await RefreshResolutionAsync(cancellationToken);
    }

    public void Clear()
    {
        _conversationId = string.Empty;
        _characterId = null;
        Presets.Clear();
        SelectedPreset = null;
        EffectiveOverlayJson = "{}";
        _effectiveSystemPrompt = string.Empty;
        DiagnosticsText = LanguageRuntime.GetString("Preset.NoneMounted");
        Status = LanguageRuntime.GetString("Preset.SelectConversation");
        OnPropertyChanged(nameof(IsConversationAvailable));
        OnPropertyChanged(nameof(IsCharacterScopeAvailable));
        RaiseCommandStates();
    }

    public string EffectiveSystemPrompt(string fallback) =>
        string.IsNullOrWhiteSpace(_effectiveSystemPrompt)
            ? fallback
            : _effectiveSystemPrompt;

    private async Task NewPresetAsync()
    {
        var name = await _interaction.EditTextAsync(
            LanguageRuntime.GetString("Preset.Create.Title"),
            LanguageRuntime.GetString("Preset.Create.Prompt"),
            LanguageRuntime.Format(
                "Preset.Create.DefaultNameFormat",
                DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss")));
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var preset = new PromptPreset
        {
            Name = name.Trim()
        };
        try
        {
            await _repository.UpsertAsync(preset);
            await ReloadPresetsAsync(preset.Id);
            Status = LanguageRuntime.Format("Preset.CreatedFormat", preset.Name);
        }
        catch (Exception exception)
        {
            Status = LanguageRuntime.Format("Preset.CreateFailedFormat", LanguageRuntime.ErrorMessage(exception));
        }
    }

    private async Task EditOverlayAsync()
    {
        if (SelectedPreset is null)
        {
            return;
        }

        var edited = await _interaction.EditTextAsync(
            LanguageRuntime.Format("Preset.EditJson.TitleFormat", SelectedPreset.Name),
            LanguageRuntime.GetString("Preset.EditJson.Prompt"),
            FormatJson(SelectedPreset.OverlayJson));
        if (edited is null)
        {
            return;
        }

        try
        {
            _ = ParseOverlay(edited);
            SelectedPreset.OverlayJson = FormatJson(edited);
            await _repository.UpsertAsync(SelectedPreset);
            await RefreshResolutionAsync();
            Status = LanguageRuntime.Format("Preset.ContentSavedFormat", SelectedPreset.Name);
        }
        catch (Exception exception)
        {
            Status = LanguageRuntime.Format("Preset.JsonSaveFailedFormat", LanguageRuntime.ErrorMessage(exception));
        }
    }

    private async Task RenameAsync()
    {
        if (SelectedPreset is null)
        {
            return;
        }

        var name = await _interaction.EditTextAsync(
            LanguageRuntime.GetString("Preset.Rename.Title"),
            LanguageRuntime.GetString("Preset.Rename.Prompt"),
            SelectedPreset.Name);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            SelectedPreset.Name = name.Trim();
            await _repository.UpsertAsync(SelectedPreset);
            await ReloadPresetsAsync(SelectedPreset.Id);
            Status = LanguageRuntime.GetString("Preset.Renamed");
        }
        catch (Exception exception)
        {
            Status = LanguageRuntime.Format("Preset.RenameFailedFormat", LanguageRuntime.ErrorMessage(exception));
        }
    }

    private async Task EditDescriptionAsync()
    {
        if (SelectedPreset is null)
        {
            return;
        }

        var description = await _interaction.EditTextAsync(
            LanguageRuntime.GetString("Preset.Description.Title"),
            LanguageRuntime.GetString("Preset.Description.Prompt"),
            string.IsNullOrWhiteSpace(SelectedPreset.Description)
                ? LanguageRuntime.GetString("Preset.Description.Default")
                : SelectedPreset.Description);
        if (description is null)
        {
            return;
        }

        SelectedPreset.Description = description;
        await _repository.UpsertAsync(SelectedPreset);
        Status = LanguageRuntime.GetString("Preset.DescriptionSaved");
    }

    private async Task DeleteAsync()
    {
        if (SelectedPreset is null
            || !_interaction.ConfirmPresetDeletion(SelectedPreset.Name))
        {
            return;
        }

        var name = SelectedPreset.Name;
        await _repository.DeleteAsync(SelectedPreset.Id);
        await ReloadPresetsAsync(null);
        await RefreshResolutionAsync();
        Status = LanguageRuntime.Format("Preset.DeletedFormat", name);
    }

    private async Task ToggleMountAsync(PresetScopeKind scopeKind)
    {
        if (SelectedPreset is null)
        {
            return;
        }

        var scopeId = ScopeId(scopeKind);
        if (scopeId is null)
        {
            Status = LanguageRuntime.GetString("Preset.ScopeUnavailable");
            return;
        }

        var mounted = scopeKind switch
        {
            PresetScopeKind.Global => GlobalMounted,
            PresetScopeKind.Character => CharacterMounted,
            _ => ConversationMounted
        };
        if (mounted)
        {
            await _repository.RemoveMountAsync(
                scopeKind,
                scopeId,
                SelectedPreset.Id);
        }
        else
        {
            await _repository.SetMountAsync(new PresetMount(
                scopeKind,
                scopeId,
                SelectedPreset.Id,
                SortIndex(scopeKind),
                IsEnabled: true));
        }

        await LoadMountStateAsync(SelectedPreset, ++_selectionVersion);
        await RefreshResolutionAsync();
        _contextChanged();
    }

    private async Task ApplyMountOrderAsync()
    {
        if (SelectedPreset is null)
        {
            return;
        }

        try
        {
            await SaveMountedOrderAsync(
                PresetScopeKind.Global,
                GlobalMounted);
            await SaveMountedOrderAsync(
                PresetScopeKind.Character,
                CharacterMounted);
            await SaveMountedOrderAsync(
                PresetScopeKind.Conversation,
                ConversationMounted);
            await RefreshResolutionAsync();
            Status = LanguageRuntime.GetString("Preset.OrderSaved");
        }
        catch (Exception exception)
        {
            Status = LanguageRuntime.Format("Preset.OrderSaveFailedFormat", LanguageRuntime.ErrorMessage(exception));
        }
    }

    private async Task SaveMountedOrderAsync(
        PresetScopeKind scopeKind,
        bool isMounted)
    {
        var scopeId = ScopeId(scopeKind);
        if (!isMounted || scopeId is null || SelectedPreset is null)
        {
            return;
        }

        await _repository.SetMountAsync(new PresetMount(
            scopeKind,
            scopeId,
            SelectedPreset.Id,
            SortIndex(scopeKind),
            IsEnabled: true));
    }

    private async Task ReloadPresetsAsync(
        string? selectedId,
        CancellationToken cancellationToken = default)
    {
        Presets.Clear();
        foreach (var preset in await _repository.ListAsync(cancellationToken))
        {
            Presets.Add(preset);
        }

        SelectedPreset = Presets.FirstOrDefault(preset => preset.Id == selectedId)
                         ?? Presets.FirstOrDefault();
    }

    private async Task LoadMountStateAsync(
        PromptPreset? preset,
        long version)
    {
        if (preset is null)
        {
            GlobalMounted = false;
            CharacterMounted = false;
            ConversationMounted = false;
            return;
        }

        try
        {
            var globalTask = FindMountAsync(
                PresetScopeKind.Global,
                "global",
                preset.Id);
            var characterTask = _characterId is null
                ? Task.FromResult<PresetMount?>(null)
                : FindMountAsync(
                    PresetScopeKind.Character,
                    _characterId,
                    preset.Id);
            var conversationTask = _conversationId.Length == 0
                ? Task.FromResult<PresetMount?>(null)
                : FindMountAsync(
                    PresetScopeKind.Conversation,
                    _conversationId,
                    preset.Id);
            await Task.WhenAll(globalTask, characterTask, conversationTask);
            if (version != _selectionVersion
                || SelectedPreset?.Id != preset.Id)
            {
                return;
            }

            ApplyMount(globalTask.Result, PresetScopeKind.Global);
            ApplyMount(characterTask.Result, PresetScopeKind.Character);
            ApplyMount(conversationTask.Result, PresetScopeKind.Conversation);
        }
        catch (Exception exception)
        {
            Status = LanguageRuntime.Format("Preset.MountReadFailedFormat", LanguageRuntime.ErrorMessage(exception));
        }
    }

    private async Task<PresetMount?> FindMountAsync(
        PresetScopeKind kind,
        string scopeId,
        string presetId)
    {
        var mounts = await _repository.ListMountsAsync(kind, scopeId);
        return mounts.FirstOrDefault(mount => mount.PresetId == presetId);
    }

    private void ApplyMount(PresetMount? mount, PresetScopeKind kind)
    {
        switch (kind)
        {
            case PresetScopeKind.Global:
                GlobalMounted = mount?.IsEnabled == true;
                GlobalSortIndex = mount?.SortIndex ?? 0;
                break;
            case PresetScopeKind.Character:
                CharacterMounted = mount?.IsEnabled == true;
                CharacterSortIndex = mount?.SortIndex ?? 0;
                break;
            case PresetScopeKind.Conversation:
                ConversationMounted = mount?.IsEnabled == true;
                ConversationSortIndex = mount?.SortIndex ?? 0;
                break;
        }
    }

    private async Task RefreshResolutionAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsConversationAvailable)
        {
            return;
        }

        var resolved = await _resolver.ResolveAsync(
            _characterId,
            _conversationId,
            cancellationToken);
        EffectiveOverlayJson = resolved.OverlayJson;
        _effectiveSystemPrompt = resolved.SystemPrompt ?? string.Empty;
        DiagnosticsText = resolved.Diagnostics.Count == 0
            ? LanguageRuntime.GetString("Preset.NoEnabledInScope")
            : string.Join(
                Environment.NewLine,
                LanguageRuntime.LocalizeDiagnostics(
                    resolved.Diagnostics,
                    "Preset.DiagnosticsSummaryFormat"));
        _contextChanged();
    }

    private string? ScopeId(PresetScopeKind scopeKind) =>
        scopeKind switch
        {
            PresetScopeKind.Global => "global",
            PresetScopeKind.Character => _characterId,
            PresetScopeKind.Conversation when _conversationId.Length > 0 =>
                _conversationId,
            _ => null
        };

    private int SortIndex(PresetScopeKind scopeKind) =>
        scopeKind switch
        {
            PresetScopeKind.Global => GlobalSortIndex,
            PresetScopeKind.Character => CharacterSortIndex,
            _ => ConversationSortIndex
        };

    private void RaiseCommandStates()
    {
        EditOverlayCommand.RaiseCanExecuteChanged();
        RenameCommand.RaiseCanExecuteChanged();
        EditDescriptionCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
        ToggleGlobalMountCommand.RaiseCanExecuteChanged();
        ToggleCharacterMountCommand.RaiseCanExecuteChanged();
        ToggleConversationMountCommand.RaiseCanExecuteChanged();
        ApplyMountOrderCommand.RaiseCanExecuteChanged();
    }

    private static JsonObject ParseOverlay(string json) =>
        JsonNode.Parse(
            json,
            documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 128
            }) as JsonObject
        ?? throw new InvalidDataException(
            LanguageRuntime.GetString("Preset.JsonRootObject"));

    private static string FormatJson(string json) =>
        ParseOverlay(json).ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true
        });
}
