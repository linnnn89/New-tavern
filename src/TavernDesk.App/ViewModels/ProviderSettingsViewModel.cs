using System.Collections.ObjectModel;
using TavernDesk.App.Presentation;
using TavernDesk.App.Services;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;

namespace TavernDesk.App.ViewModels;

public sealed class ProviderSettingsViewModel : ViewModelBase
{
    private readonly IProviderProfileRepository _repository;
    private readonly IModelCatalogRepository _models;
    private readonly IModelAssignmentRepository _assignments;
    private readonly ISecretStore _secrets;
    private readonly IProviderGateway _gateway;
    private readonly IContextBudgetProvider _contextBudget;
    private readonly IUserInteractionService _interaction;
    private readonly HashSet<string> _persistedProfileIds = new(StringComparer.Ordinal);
    private readonly List<ProviderModel> _allCatalogModels = [];
    private readonly List<ProviderModel> _allAssignmentModels = [];
    private ProviderEditBuffer _editor = new();
    private ProviderProfile? _selectedProfile;
    private ProviderProfile? _catalogProvider;
    private ProviderProfile? _assignmentProvider;
    private ProviderModel? _selectedCatalogModel;
    private ProviderModel? _selectedAssignmentModel;
    private ModelFunctionOption _selectedFunction;
    private string _pendingApiKey = string.Empty;
    private string _keyStatus = "未保存 API Key。";
    private string _catalogSearchText = string.Empty;
    private string _assignmentSearchText = string.Empty;
    private string _modelContextLimit = "32768";
    private string _modelMaxOutputTokens = "4096";
    private string _assignmentContextLimit = "32768";
    private string _assignmentMaxOutputTokens = "4096";
    private string _assignmentTemperature = "0.8";
    private string _assignmentTopP = "1";
    private string _status = "保存接入商不会联网；只有主动点击“刷新模型目录”才会请求对应 API。";
    private int _selectedSettingsTabIndex;
    private int _catalogLoadVersion;
    private int _assignmentLoadVersion;

    public ProviderSettingsViewModel(
        IProviderProfileRepository repository,
        IModelCatalogRepository models,
        IModelAssignmentRepository assignments,
        ISecretStore secrets,
        IProviderGateway gateway,
        IContextBudgetProvider contextBudget,
        IUserInteractionService interaction,
        IGlobalPromptConfiguration globalPrompts,
        IFileDialogService fileDialog)
    {
        _repository = repository;
        _models = models;
        _assignments = assignments;
        _secrets = secrets;
        _gateway = gateway;
        _contextBudget = contextBudget;
        _interaction = interaction;
        Prompts = new PromptSettingsViewModel(globalPrompts, fileDialog);
        FunctionOptions =
        [
            new(ModelFunctionKind.Chat, "角色聊天"),
            new(ModelFunctionKind.MemoryUpdate, "记忆银行更新"),
            new(ModelFunctionKind.MemoryCompression, "记忆银行压缩"),
            new(ModelFunctionKind.GroupChat, "群聊接力"),
            new(ModelFunctionKind.GroupMemoryMerge, "群聊记忆合并")
        ];
        _selectedFunction = FunctionOptions[0];

        AddCustomCommand = new AsyncRelayCommand(AddCustomAsync);
        DeleteProviderCommand = new AsyncRelayCommand(
            DeleteProviderAsync,
            parameter => parameter is ProviderProfile);
        SaveCommand = new AsyncRelayCommand(
            SaveProviderAsync,
            () => SelectedProfile is not null);
        ClearKeyCommand = new AsyncRelayCommand(
            ClearKeyAsync,
            () => SelectedProfile is { SecretReference.Length: > 0 });
        RefreshModelsCommand = new AsyncRelayCommand(
            RefreshModelsAsync,
            () => CatalogProvider is not null);
        SaveModelLimitsCommand = new AsyncRelayCommand(
            SaveModelLimitsAsync,
            () => SelectedCatalogModel is not null);
        SaveAssignmentCommand = new AsyncRelayCommand(
            SaveAssignmentAsync,
            () => AssignmentProvider is not null
                  && SelectedAssignmentModel is not null);
        ToggleReasoningCommand = new AsyncRelayCommand(
            ToggleReasoningAsync,
            parameter => parameter is ModelFunctionAssignmentOverview
            {
                IsReasoningAvailable: true
            });
        _editor.PropertyChanged += OnEditorPropertyChanged;
    }

    public ObservableCollection<ProviderProfile> Profiles { get; } = [];
    public ObservableCollection<ProviderModel> VisibleCatalogModels { get; } = [];
    public ObservableCollection<ProviderModel> VisibleAssignmentModels { get; } = [];
    public ObservableCollection<ModelFunctionAssignmentOverview> AssignmentOverview { get; } = [];
    public PromptSettingsViewModel Prompts { get; }
    public ProviderEditBuffer Editor
    {
        get => _editor;
        private set
        {
            if (ReferenceEquals(_editor, value))
            {
                return;
            }

            _editor.PropertyChanged -= OnEditorPropertyChanged;
            if (SetProperty(ref _editor, value))
            {
                _editor.PropertyChanged += OnEditorPropertyChanged;
                OnPropertyChanged(nameof(HasUnsavedChanges));
            }
        }
    }
    public IReadOnlyList<ProviderAdapterKind> AdapterKinds { get; } =
        Enum.GetValues<ProviderAdapterKind>();
    public IReadOnlyList<ModelFunctionOption> FunctionOptions { get; }
    public AsyncRelayCommand AddCustomCommand { get; }
    public AsyncRelayCommand DeleteProviderCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand ClearKeyCommand { get; }
    public AsyncRelayCommand RefreshModelsCommand { get; }
    public AsyncRelayCommand SaveModelLimitsCommand { get; }
    public AsyncRelayCommand SaveAssignmentCommand { get; }
    public AsyncRelayCommand ToggleReasoningCommand { get; }

    public ProviderProfile? SelectedProfile
    {
        get => _selectedProfile;
        private set
        {
            if (!SetProperty(ref _selectedProfile, value))
            {
                return;
            }

            SaveCommand.RaiseCanExecuteChanged();
            ClearKeyCommand.RaiseCanExecuteChanged();
        }
    }

    public ProviderProfile? CatalogProvider
    {
        get => _catalogProvider;
        set
        {
            if (!SetProperty(ref _catalogProvider, value))
            {
                return;
            }

            var version = ++_catalogLoadVersion;
            _ = LoadCatalogSafeAsync(version);
            RefreshModelsCommand.RaiseCanExecuteChanged();
        }
    }

    public ProviderProfile? AssignmentProvider
    {
        get => _assignmentProvider;
        set
        {
            if (!SetProperty(ref _assignmentProvider, value))
            {
                return;
            }

            var version = ++_assignmentLoadVersion;
            _ = LoadAssignmentModelsSafeAsync(version, preferredModelId: null);
            SaveAssignmentCommand.RaiseCanExecuteChanged();
        }
    }

    public ProviderModel? SelectedCatalogModel
    {
        get => _selectedCatalogModel;
        set
        {
            if (!SetProperty(ref _selectedCatalogModel, value))
            {
                return;
            }

            ModelContextLimit = (value?.ContextLimit ?? 32768).ToString();
            ModelMaxOutputTokens = (value?.MaxOutputTokens ?? 4096).ToString();
            SaveModelLimitsCommand.RaiseCanExecuteChanged();
        }
    }

    public ProviderModel? SelectedAssignmentModel
    {
        get => _selectedAssignmentModel;
        set
        {
            if (!SetProperty(ref _selectedAssignmentModel, value))
            {
                return;
            }

            if (value is not null)
            {
                AssignmentContextLimit = value.ContextLimit.ToString();
                AssignmentMaxOutputTokens = value.MaxOutputTokens.ToString();
            }

            SaveAssignmentCommand.RaiseCanExecuteChanged();
        }
    }

    public ModelFunctionOption SelectedFunction
    {
        get => _selectedFunction;
        set
        {
            if (SetProperty(ref _selectedFunction, value))
            {
                var version = ++_assignmentLoadVersion;
                _ = LoadFunctionAssignmentSafeAsync(version);
            }
        }
    }

    public string PendingApiKey
    {
        get => _pendingApiKey;
        set
        {
            if (SetProperty(ref _pendingApiKey, value))
            {
                OnPropertyChanged(nameof(HasUnsavedChanges));
                KeyStatus = value.Length == 0
                    ? KeyStatusFor(SelectedProfile)
                    : "已输入新的 API Key；尚未保存，不会显示或写入数据库。";
            }
        }
    }

    public string KeyStatus
    {
        get => _keyStatus;
        private set => SetProperty(ref _keyStatus, value);
    }

    public bool HasUnsavedChanges =>
        Editor.IsDirty || PendingApiKey.Length > 0;

    public string CatalogSearchText
    {
        get => _catalogSearchText;
        set
        {
            if (SetProperty(ref _catalogSearchText, value))
            {
                ApplyCatalogFilter();
            }
        }
    }

    public string AssignmentSearchText
    {
        get => _assignmentSearchText;
        set
        {
            if (SetProperty(ref _assignmentSearchText, value))
            {
                ApplyAssignmentFilter();
            }
        }
    }

    public string ModelContextLimit
    {
        get => _modelContextLimit;
        set => SetProperty(ref _modelContextLimit, value);
    }

    public string ModelMaxOutputTokens
    {
        get => _modelMaxOutputTokens;
        set => SetProperty(ref _modelMaxOutputTokens, value);
    }

    public string AssignmentContextLimit
    {
        get => _assignmentContextLimit;
        set => SetProperty(ref _assignmentContextLimit, value);
    }

    public string AssignmentMaxOutputTokens
    {
        get => _assignmentMaxOutputTokens;
        set => SetProperty(ref _assignmentMaxOutputTokens, value);
    }

    public string AssignmentTemperature
    {
        get => _assignmentTemperature;
        set => SetProperty(ref _assignmentTemperature, value);
    }

    public string AssignmentTopP
    {
        get => _assignmentTopP;
        set => SetProperty(ref _assignmentTopP, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public int SelectedSettingsTabIndex
    {
        get => _selectedSettingsTabIndex;
        set => SetProperty(ref _selectedSettingsTabIndex, value);
    }

    public async Task LoadAsync()
    {
        var selectedProfileId = SelectedProfile?.Id;
        var catalogProviderId = CatalogProvider?.Id;
        var assignmentProviderId = AssignmentProvider?.Id;
        await ReloadProfilesAsync();
        SelectProfile(
            Profiles.FirstOrDefault(profile => profile.Id == selectedProfileId)
            ?? Profiles.FirstOrDefault());
        CatalogProvider = Profiles.FirstOrDefault(profile => profile.Id == catalogProviderId)
                          ?? Profiles.FirstOrDefault();
        _assignmentProvider = Profiles.FirstOrDefault(
                                  profile => profile.Id == assignmentProviderId)
                              ?? Profiles.FirstOrDefault();
        OnPropertyChanged(nameof(AssignmentProvider));
        await LoadFunctionAssignmentSafeAsync(++_assignmentLoadVersion);
        await RefreshAssignmentOverviewAsync();
        await Prompts.LoadAsync();
    }

    public void OpenPrompt(GlobalPromptKey key)
    {
        SelectedSettingsTabIndex = 3;
        Prompts.Open(key);
        Status = "已定位到对应的全局提示词；修改后请保存。";
    }

    public async Task<bool> ConfirmCanLeaveAsync()
    {
        if (!HasUnsavedChanges)
        {
            return true;
        }

        switch (_interaction.ConfirmUnsavedProviderChanges(
                    Editor.Name.Length == 0 ? "未命名接入商" : Editor.Name))
        {
            case UnsavedChangesDecision.Cancel:
                return false;
            case UnsavedChangesDecision.Save:
                await SaveProviderAsync();
                return !HasUnsavedChanges;
            case UnsavedChangesDecision.Discard:
                await DiscardCurrentEditsAsync();
                return true;
            default:
                return false;
        }
    }

    private async Task ReloadProfilesAsync()
    {
        Profiles.Clear();
        _persistedProfileIds.Clear();
        foreach (var profile in await _repository.ListAsync())
        {
            Profiles.Add(profile);
            _persistedProfileIds.Add(profile.Id);
        }
    }

    private async Task AddCustomAsync()
    {
        if (!await ConfirmCanLeaveAsync())
        {
            return;
        }

        var profile = new ProviderProfile
        {
            Name = "自定义接入商",
            AdapterKind = ProviderAdapterKind.OpenAiCompatible,
            BaseUrl = "http://127.0.0.1:1234/v1"
        };
        Profiles.Add(profile);
        SelectProfile(profile);
        Editor.MarkDirty();
        Status = "新接入商尚未保存。";
    }

    private async Task DeleteProviderAsync(object? parameter)
    {
        if (parameter is not ProviderProfile requested)
        {
            return;
        }

        var profile = Profiles.FirstOrDefault(item => item.Id == requested.Id);
        if (profile is null)
        {
            return;
        }

        if (SelectedProfile?.Id == profile.Id
            && HasUnsavedChanges
            && !await ConfirmCanLeaveAsync())
        {
            return;
        }

        profile = Profiles.FirstOrDefault(item => item.Id == requested.Id);
        if (profile is null
            || !_interaction.ConfirmProviderDeletion(profile.Name))
        {
            return;
        }

        var secretReference = profile.SecretReference;
        if (secretReference.Length > 0)
        {
            try
            {
                await _secrets.DeleteAsync(secretReference);
                profile.SecretReference = string.Empty;
            }
            catch (Exception exception)
            {
                Status =
                    $"删除已取消：未能清除“{profile.Name}”的 DPAPI Key 文件：{exception.Message}";
                return;
            }
        }

        try
        {
            if (_persistedProfileIds.Contains(profile.Id))
            {
                await _repository.DeleteAsync(profile.Id);
            }
        }
        catch (Exception exception)
        {
            if (secretReference.Length > 0)
            {
                try
                {
                    await _repository.UpsertAsync(profile);
                }
                catch
                {
                    // The Key is already gone. Keep the primary delete error visible.
                }
            }

            Status = secretReference.Length > 0
                ? $"DPAPI Key 已清除，但接入商删除失败：{exception.Message}"
                : $"接入商删除失败：{exception.Message}";
            return;
        }

        Profiles.Remove(profile);
        _persistedProfileIds.Remove(profile.Id);
        if (SelectedProfile?.Id == profile.Id)
        {
            SelectProfile(Profiles.FirstOrDefault());
        }

        if (CatalogProvider?.Id == profile.Id)
        {
            CatalogProvider = Profiles.FirstOrDefault();
        }

        if (AssignmentProvider?.Id == profile.Id)
        {
            _assignmentProvider = Profiles.FirstOrDefault();
            OnPropertyChanged(nameof(AssignmentProvider));
        }

        await LoadFunctionAssignmentSafeAsync(++_assignmentLoadVersion);
        await RefreshAssignmentOverviewAsync();
        Status =
            $"已删除“{profile.Name}”；模型目录、功能分配和本地 DPAPI Key 已清理。";
    }

    public async Task<bool> SelectProfileAsync(ProviderProfile profile)
    {
        if (profile.Id == SelectedProfile?.Id)
        {
            return true;
        }

        if (!await ConfirmCanLeaveAsync())
        {
            return false;
        }

        SelectProfile(profile);
        return true;
    }

    private void SelectProfile(ProviderProfile? profile)
    {
        SelectedProfile = profile;
        PendingApiKey = string.Empty;
        if (profile is null)
        {
            KeyStatus = "未选择接入商。";
            return;
        }

        var editor = new ProviderEditBuffer();
        editor.Load(profile);
        Editor = editor;
        KeyStatus = KeyStatusFor(profile);
    }

    private void OnEditorPropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ProviderEditBuffer.IsDirty))
        {
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
    }

    private async Task SaveProviderAsync()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        if (!Editor.TryApplyTo(SelectedProfile, out var error))
        {
            Status = error;
            return;
        }

        if (SelectedProfile.AdapterKind == ProviderAdapterKind.GrokCli
            && !string.IsNullOrWhiteSpace(PendingApiKey))
        {
            Status = "Grok CLI 使用本机 grok login 的订阅登录，不接收或保存 API Key。";
            return;
        }

        var previousReference = SelectedProfile.SecretReference;
        string? newlySavedReference = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(PendingApiKey))
            {
                newlySavedReference = await _secrets.SaveAsync(
                    SelectedProfile.Id,
                    PendingApiKey.Trim());
                SelectedProfile.SecretReference = newlySavedReference;
            }

            await _repository.UpsertAsync(SelectedProfile);
        }
        catch
        {
            SelectedProfile.SecretReference = previousReference;
            if (newlySavedReference is not null)
            {
                try
                {
                    await _secrets.DeleteAsync(newlySavedReference);
                }
                catch
                {
                    // The database still points to the previous reference.
                    // A protected, unreferenced file is safer than losing that state.
                }
            }

            throw;
        }

        var cleanupWarning = string.Empty;
        if (newlySavedReference is not null
            && previousReference.Length > 0
            && !string.Equals(
                previousReference,
                newlySavedReference,
                StringComparison.Ordinal))
        {
            try
            {
                await _secrets.DeleteAsync(previousReference);
            }
            catch (Exception exception)
            {
                cleanupWarning =
                    $"；旧密钥引用已停用，但保护文件清理失败：{exception.Message}";
            }
        }

        _persistedProfileIds.Add(SelectedProfile.Id);
        Editor.MarkSaved();
        PendingApiKey = string.Empty;
        KeyStatus = KeyStatusFor(SelectedProfile);
        Status = $"已保存：{SelectedProfile.Name}；没有发起网络请求{cleanupWarning}。";
        await RefreshProfileReferencesAsync(SelectedProfile.Id);
    }

    private async Task ClearKeyAsync()
    {
        if (SelectedProfile is null
            || string.IsNullOrWhiteSpace(SelectedProfile.SecretReference)
            || !_interaction.ConfirmSecretClear(SelectedProfile.Name))
        {
            return;
        }

        var previousReference = SelectedProfile.SecretReference;
        SelectedProfile.SecretReference = string.Empty;
        SelectedProfile.UpdatedAt = DateTimeOffset.Now;
        try
        {
            await _repository.UpsertAsync(SelectedProfile);
        }
        catch
        {
            SelectedProfile.SecretReference = previousReference;
            throw;
        }

        var cleanupWarning = string.Empty;
        try
        {
            await _secrets.DeleteAsync(previousReference);
        }
        catch (Exception exception)
        {
            cleanupWarning =
                $"；数据库已停用该密钥，但保护文件清理失败：{exception.Message}";
        }

        PendingApiKey = string.Empty;
        KeyStatus = "API Key 已清除。";
        ClearKeyCommand.RaiseCanExecuteChanged();
        Status = $"已清除 {SelectedProfile.Name} 的本机密钥{cleanupWarning}。";
    }

    private async Task DiscardCurrentEditsAsync()
    {
        PendingApiKey = string.Empty;
        if (SelectedProfile is null)
        {
            return;
        }

        if (!_persistedProfileIds.Contains(SelectedProfile.Id))
        {
            Profiles.Remove(SelectedProfile);
            SelectProfile(Profiles.FirstOrDefault());
        }
        else
        {
            var stored = await _repository.GetAsync(SelectedProfile.Id);
            if (stored is not null)
            {
                var index = Profiles.IndexOf(SelectedProfile);
                if (index >= 0)
                {
                    Profiles[index] = stored;
                }

                SelectProfile(stored);
            }
        }
    }

    private async Task RefreshProfileReferencesAsync(string selectedId)
    {
        var catalogId = CatalogProvider?.Id;
        var assignmentId = AssignmentProvider?.Id;
        await ReloadProfilesAsync();
        SelectProfile(Profiles.FirstOrDefault(profile => profile.Id == selectedId));
        CatalogProvider = Profiles.FirstOrDefault(profile => profile.Id == catalogId)
                          ?? Profiles.FirstOrDefault();
        _assignmentProvider = Profiles.FirstOrDefault(profile => profile.Id == assignmentId)
                              ?? Profiles.FirstOrDefault();
        OnPropertyChanged(nameof(AssignmentProvider));
        await RefreshAssignmentOverviewAsync();
    }

    private async Task RefreshModelsAsync()
    {
        if (CatalogProvider is null)
        {
            return;
        }

        if (!_persistedProfileIds.Contains(CatalogProvider.Id))
        {
            Status = "请先保存接入商，再刷新模型目录。";
            return;
        }

        try
        {
            Status = $"正在请求 {CatalogProvider.Name} 的模型目录…";
            var descriptors = await _gateway.RefreshModelsAsync(CatalogProvider.Id);
            await _models.ReplaceAsync(CatalogProvider.Id, descriptors);
            await LoadCatalogSafeAsync(++_catalogLoadVersion);
            Status = $"已刷新 {CatalogProvider.Name}：{descriptors.Count} 个模型。";
        }
        catch (Exception exception)
        {
            Status = $"刷新模型失败：{exception.Message}";
        }
    }

    private async Task LoadCatalogSafeAsync(int version)
    {
        try
        {
            var provider = CatalogProvider;
            var models = provider is null
                ? Array.Empty<ProviderModel>()
                : await _models.ListAsync(provider.Id);
            if (version != _catalogLoadVersion)
            {
                return;
            }

            _allCatalogModels.Clear();
            _allCatalogModels.AddRange(models);
            ApplyCatalogFilter();
        }
        catch (Exception exception) when (version == _catalogLoadVersion)
        {
            Status = $"读取模型目录失败：{exception.Message}";
        }
    }

    private void ApplyCatalogFilter()
    {
        var selectedId = SelectedCatalogModel?.ModelId;
        var query = CatalogSearchText.Trim();
        VisibleCatalogModels.Clear();
        foreach (var model in _allCatalogModels.Where(model =>
                     query.Length == 0
                     || model.ModelId.Contains(query, StringComparison.OrdinalIgnoreCase)
                     || model.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            VisibleCatalogModels.Add(model);
        }

        SelectedCatalogModel = VisibleCatalogModels.FirstOrDefault(model =>
                                   model.ModelId == selectedId)
                               ?? VisibleCatalogModels.FirstOrDefault();
    }

    private async Task SaveModelLimitsAsync()
    {
        if (SelectedCatalogModel is null)
        {
            Status = "请先选择一个模型。";
            return;
        }

        if (!TryReadLimits(
                ModelContextLimit,
                ModelMaxOutputTokens,
                out var contextLimit,
                out var maxOutput,
                out var error))
        {
            Status = error;
            return;
        }

        SelectedCatalogModel.ContextLimit = contextLimit;
        SelectedCatalogModel.MaxOutputTokens = maxOutput;
        SelectedCatalogModel.UpdatedAt = DateTimeOffset.Now;
        await _models.UpsertAsync(SelectedCatalogModel);
        Status = $"已保存 {SelectedCatalogModel.ModelId} 的本地 Token 上限。";
    }

    private async Task LoadFunctionAssignmentSafeAsync(int version)
    {
        try
        {
            var assignment = await _assignments.GetAsync(SelectedFunction.Value);
            if (version != _assignmentLoadVersion)
            {
                return;
            }

            var provider = Profiles.FirstOrDefault(profile =>
                               profile.Id == assignment?.ProviderId)
                           ?? AssignmentProvider
                           ?? Profiles.FirstOrDefault();
            _assignmentProvider = provider;
            OnPropertyChanged(nameof(AssignmentProvider));
            if (assignment is not null)
            {
                AssignmentContextLimit = assignment.ContextLimit.ToString();
                AssignmentMaxOutputTokens = assignment.MaxOutputTokens.ToString();
                AssignmentTemperature = assignment.Temperature.ToString("0.###");
                AssignmentTopP = assignment.TopP.ToString("0.###");
            }
            else
            {
                AssignmentContextLimit = "32768";
                AssignmentMaxOutputTokens = "4096";
                AssignmentTemperature = "0.8";
                AssignmentTopP = "1";
            }

            await LoadAssignmentModelsSafeAsync(
                version,
                assignment?.ModelId);
        }
        catch (Exception exception) when (version == _assignmentLoadVersion)
        {
            Status = $"读取功能分配失败：{exception.Message}";
        }
    }

    private async Task RefreshAssignmentOverviewAsync()
    {
        var assignments = (await _assignments.ListAsync())
            .ToDictionary(assignment => assignment.FunctionKind);
        AssignmentOverview.Clear();
        foreach (var option in FunctionOptions)
        {
            assignments.TryGetValue(option.Value, out var assignment);
            var provider = assignment is null
                ? null
                : Profiles.FirstOrDefault(profile =>
                    profile.Id == assignment.ProviderId);
            var reasoningAvailable =
                assignment is not null
                && ModelFeatureSupport.SupportsOpenRouterDeepSeekReasoning(
                    provider,
                    assignment.ModelId);
            AssignmentOverview.Add(new ModelFunctionAssignmentOverview(
                option.Value,
                option.Label,
                assignment is null
                    ? "未分配"
                    : provider?.Name ?? "接入商已删除",
                assignment?.ModelId ?? "—",
                reasoningAvailable,
                reasoningAvailable && assignment!.ReasoningEnabled));
        }
    }

    private async Task LoadAssignmentModelsSafeAsync(
        int version,
        string? preferredModelId)
    {
        try
        {
            var provider = AssignmentProvider;
            var models = provider is null
                ? Array.Empty<ProviderModel>()
                : await _models.ListAsync(provider.Id);
            if (version != _assignmentLoadVersion)
            {
                return;
            }

            _allAssignmentModels.Clear();
            _allAssignmentModels.AddRange(models);
            ApplyAssignmentFilter(preferredModelId);
        }
        catch (Exception exception) when (version == _assignmentLoadVersion)
        {
            Status = $"读取分配模型失败：{exception.Message}";
        }
    }

    private void ApplyAssignmentFilter(string? preferredModelId = null)
    {
        var selectedId = preferredModelId ?? SelectedAssignmentModel?.ModelId;
        var query = AssignmentSearchText.Trim();
        VisibleAssignmentModels.Clear();
        foreach (var model in _allAssignmentModels.Where(model =>
                     query.Length == 0
                     || model.ModelId.Contains(query, StringComparison.OrdinalIgnoreCase)
                     || model.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            VisibleAssignmentModels.Add(model);
        }

        SelectedAssignmentModel = VisibleAssignmentModels.FirstOrDefault(model =>
                                      model.ModelId == selectedId)
                                  ?? VisibleAssignmentModels.FirstOrDefault();
    }

    private async Task SaveAssignmentAsync()
    {
        if (AssignmentProvider is null || SelectedAssignmentModel is null)
        {
            Status = "请先选择供应商，再从搜索结果中选择具体模型。";
            return;
        }

        if (!TryReadLimits(
                AssignmentContextLimit,
                AssignmentMaxOutputTokens,
                out var contextLimit,
                out var maxOutput,
                out var error))
        {
            Status = error;
            return;
        }

        if (!double.TryParse(AssignmentTemperature, out var temperature)
            || temperature is < 0 or > 2)
        {
            Status = "temperature 必须在 0–2 之间。";
            return;
        }

        if (!double.TryParse(AssignmentTopP, out var topP)
            || topP is <= 0 or > 1)
        {
            Status = "top_p 必须在 0（不含）–1 之间。";
            return;
        }

        var previous = await _assignments.GetAsync(SelectedFunction.Value);
        var reasoningAvailable =
            ModelFeatureSupport.SupportsOpenRouterDeepSeekReasoning(
                AssignmentProvider,
                SelectedAssignmentModel.ModelId);
        var assignment = new ModelFunctionAssignment
        {
            FunctionKind = SelectedFunction.Value,
            ProviderId = AssignmentProvider.Id,
            ModelId = SelectedAssignmentModel.ModelId,
            ContextLimit = contextLimit,
            MaxOutputTokens = maxOutput,
            Temperature = temperature,
            TopP = topP,
            ReasoningEnabled =
                reasoningAvailable
                && previous?.ProviderId == AssignmentProvider.Id
                && previous.ModelId == SelectedAssignmentModel.ModelId
                && previous.ReasoningEnabled,
            UpdatedAt = DateTimeOffset.Now
        };
        await _assignments.UpsertAsync(assignment);
        if (assignment.FunctionKind == ModelFunctionKind.Chat)
        {
            _contextBudget.UpdateBudget(new ContextBudget(
                assignment.ContextLimit,
                assignment.MaxOutputTokens,
                $"{AssignmentProvider.Name} / {assignment.ModelId}",
                assignment.ModelId));
        }

        await RefreshAssignmentOverviewAsync();
        Status =
            $"已把“{SelectedFunction.Label}”分配给 {AssignmentProvider.Name} / {assignment.ModelId}。";
    }

    private async Task ToggleReasoningAsync(object? parameter)
    {
        if (parameter is not ModelFunctionAssignmentOverview overview)
        {
            return;
        }

        var assignment = await _assignments.GetAsync(overview.Value);
        var provider = assignment is null
            ? null
            : Profiles.FirstOrDefault(item => item.Id == assignment.ProviderId);
        if (assignment is null
            || !ModelFeatureSupport.SupportsOpenRouterDeepSeekReasoning(
                provider,
                assignment.ModelId))
        {
            Status = "该分配不是 OpenRouter DeepSeek 模型，未修改推理设置。";
            await RefreshAssignmentOverviewAsync();
            return;
        }

        assignment.ReasoningEnabled = !assignment.ReasoningEnabled;
        assignment.UpdatedAt = DateTimeOffset.Now;
        await _assignments.UpsertAsync(assignment);
        await RefreshAssignmentOverviewAsync();
        Status =
            $"“{overview.Label}”的 DeepSeek 推理已{(assignment.ReasoningEnabled ? "开启" : "关闭")}。";
    }

    private static bool TryReadLimits(
        string contextText,
        string outputText,
        out int contextLimit,
        out int maxOutput,
        out string error)
    {
        error = string.Empty;
        if (!int.TryParse(contextText, out contextLimit)
            || contextLimit is < 1024 or > 4_194_304)
        {
            maxOutput = 0;
            error = "上下文上限必须是 1024–4194304 之间的整数。";
            return false;
        }

        if (!int.TryParse(outputText, out maxOutput)
            || maxOutput < 1
            || maxOutput > contextLimit)
        {
            error = "输出上限必须是正整数，且不能超过上下文上限。";
            return false;
        }

        return true;
    }

    private static string KeyStatusFor(ProviderProfile? profile) =>
        profile switch
        {
            { AdapterKind: ProviderAdapterKind.GrokCli, SecretReference.Length: > 0 } =>
                "Grok CLI 不使用此 API Key；建议清除旧 Key，并在终端执行 grok login。",
            { AdapterKind: ProviderAdapterKind.GrokCli } =>
                "Grok CLI 使用 grok login 的订阅登录；这里不需要 API Key。",
            { SecretReference.Length: > 0 } =>
                "API Key：已受 Windows DPAPI（当前用户）保护保存。",
            _ => "未保存 API Key；无需鉴权的兼容服务可以留空。"
        };
}

public sealed record ModelFunctionOption(
    ModelFunctionKind Value,
    string Label);

public sealed record ModelFunctionAssignmentOverview(
    ModelFunctionKind Value,
    string Label,
    string ProviderName,
    string ModelId,
    bool IsReasoningAvailable = false,
    bool IsReasoningEnabled = false)
{
    public string TargetText => ModelId == "—"
        ? ProviderName
        : $"{ProviderName} / {ModelId}";

    public string ReasoningStateText =>
        IsReasoningEnabled ? "ON" : "OFF";
}
