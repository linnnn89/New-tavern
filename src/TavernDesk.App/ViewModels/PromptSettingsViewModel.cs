using System.Collections.ObjectModel;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using TavernDesk.App.Presentation;
using TavernDesk.App.Services;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;

namespace TavernDesk.App.ViewModels;

public sealed class PromptSettingsViewModel : ViewModelBase
{
    private static readonly JsonSerializerOptions ExportJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly IGlobalPromptConfiguration _configuration;
    private readonly IFileDialogService _fileDialog;
    private readonly Dictionary<GlobalPromptKey, string> _saved = [];
    private PromptCategoryViewModel? _selectedCategory;
    private PromptEditorItemViewModel? _selectedPrompt;
    private string _status =
        "这里保存全局默认；角色卡、剧本和仍开放的局部提示词可按各模块规则追加或覆盖。";

    public PromptSettingsViewModel(
        IGlobalPromptConfiguration configuration,
        IFileDialogService fileDialog)
    {
        _configuration = configuration;
        _fileDialog = fileDialog;
        Categories =
        [
            new(
                "聊天",
                [
                    Create(
                        GlobalPromptKey.ChatSystem,
                        "角色聊天 · 全局 System Prompt",
                        "所有个人角色聊天共享的基础扮演职责；角色卡的 System Prompt 会在其后补充。")
                ]),
            new(
                "记忆银行",
                [
                    Create(
                        GlobalPromptKey.MemoryUpdateSystem,
                        "记忆更新 · 全局提示词",
                        "唯一可编辑提示词；旧记忆、新增记录和目标 tokens 由程序作为固定数据载荷附加。"),
                    Create(
                        GlobalPromptKey.MemoryCompressionSystem,
                        "记忆压缩 · 全局提示词",
                        "唯一可编辑提示词；待压缩记忆和目标 tokens 由程序作为固定数据载荷附加。")
                ]),
            new(
                "群聊",
                [
                    Create(
                        GlobalPromptKey.GroupRelaySystem,
                        "群聊接力 · System Prompt",
                        "规定当前发言角色如何在多人场景中行动，不替其他成员作答。"),
                    Create(
                        GlobalPromptKey.GroupMemoryMergeSystem,
                        "群聊记忆合并 · 全局提示词",
                        "唯一可编辑提示词；角色记忆、群聊记忆、角色名和目标 tokens 由程序作为固定数据载荷附加。")
                ]),
            new(
                "跑团",
                [
                    Create(
                        GlobalPromptKey.CampaignGmSystem,
                        "跑团 · GM 职责",
                        "固定 GM 的裁判、世界事实写入和信息隔离职责；剧本专用说明会在其后追加。"),
                    Create(
                        GlobalPromptKey.CampaignPlayerSystem,
                        "跑团 · AI 玩家职责",
                        "固定 AI 玩家只声明自身行动、不替 GM 判定结果的职责。")
                ])
        ];
        NavigationItems = new ObservableCollection<PromptNavigationItemViewModel>(
            Categories.SelectMany(category =>
                category.Prompts.Select(prompt =>
                    new PromptNavigationItemViewModel(category.Name, prompt))));
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => HasUnsavedChanges);
        ExportCommand = new AsyncRelayCommand(ExportAsync);
        RestoreDefaultCommand = new RelayCommand(
            RestoreSelectedDefault,
            () => SelectedPrompt is not null);
        SelectedCategory = Categories[0];
    }

    public ObservableCollection<PromptCategoryViewModel> Categories { get; }
    public ObservableCollection<PromptNavigationItemViewModel> NavigationItems { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand ExportCommand { get; }
    public RelayCommand RestoreDefaultCommand { get; }

    public PromptCategoryViewModel? SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (!SetProperty(ref _selectedCategory, value))
            {
                return;
            }

            SelectedPrompt = value?.Prompts.FirstOrDefault();
        }
    }

    public PromptEditorItemViewModel? SelectedPrompt
    {
        get => _selectedPrompt;
        set
        {
            if (SetProperty(ref _selectedPrompt, value))
            {
                var category = value is null
                    ? null
                    : Categories.FirstOrDefault(item => item.Prompts.Contains(value));
                if (!ReferenceEquals(_selectedCategory, category))
                {
                    _selectedCategory = category;
                    OnPropertyChanged(nameof(SelectedCategory));
                }

                RestoreDefaultCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasUnsavedChanges =>
        AllPrompts().Any(item =>
            !_saved.TryGetValue(item.Key, out var saved)
            || !string.Equals(saved, item.Text, StringComparison.Ordinal));

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public Task LoadAsync()
    {
        if (HasUnsavedChanges && _saved.Count > 0)
        {
            return Task.CompletedTask;
        }

        _saved.Clear();
        var snapshot = _configuration.Snapshot();
        foreach (var item in AllPrompts())
        {
            var value = snapshot.GetValueOrDefault(
                item.Key,
                GlobalPromptDefaults.Get(item.Key));
            item.Load(value);
            _saved[item.Key] = value;
        }

        RaiseDirtyState();
        return Task.CompletedTask;
    }

    public void Open(GlobalPromptKey key)
    {
        var category = Categories.FirstOrDefault(item =>
            item.Prompts.Any(prompt => prompt.Key == key));
        var prompt = category?.Prompts.FirstOrDefault(item => item.Key == key);
        if (category is not null && prompt is not null)
        {
            SelectedCategory = category;
            SelectedPrompt = prompt;
        }
    }

    private PromptEditorItemViewModel Create(
        GlobalPromptKey key,
        string label,
        string description) =>
        new(key, label, description, RaiseDirtyState);

    private IEnumerable<PromptEditorItemViewModel> AllPrompts() =>
        Categories.SelectMany(category => category.Prompts);

    private async Task SaveAsync()
    {
        var values = AllPrompts().ToDictionary(item => item.Key, item => item.Text);
        await _configuration.SaveAsync(values);
        _saved.Clear();
        foreach (var item in AllPrompts())
        {
            _saved[item.Key] = item.Text;
        }

        Status = "已保存全局提示词配置；后续模型请求立即使用新值。";
        RaiseDirtyState();
    }

    private async Task ExportAsync()
    {
        var path = _fileDialog.PickPromptProfileExportPath();
        if (path is null)
        {
            return;
        }

        var profile = new GlobalPromptProfile
        {
            Prompts = AllPrompts().ToDictionary(
                item => item.Key.ToString(),
                item => item.Text,
                StringComparer.Ordinal)
        };
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(profile, ExportJsonOptions));
        Status = $"已另存当前完整提示词配置：{Path.GetFileName(path)}";
    }

    private void RestoreSelectedDefault()
    {
        if (SelectedPrompt is null)
        {
            return;
        }

        SelectedPrompt.Text = GlobalPromptDefaults.Get(SelectedPrompt.Key);
        Status = $"“{SelectedPrompt.Label}”已恢复到内置默认；点击保存后生效。";
    }

    private void RaiseDirtyState()
    {
        OnPropertyChanged(nameof(HasUnsavedChanges));
        SaveCommand.RaiseCanExecuteChanged();
    }
}

public sealed class PromptCategoryViewModel
{
    public PromptCategoryViewModel(
        string name,
        IEnumerable<PromptEditorItemViewModel> prompts)
    {
        Name = name;
        Prompts = new ObservableCollection<PromptEditorItemViewModel>(prompts);
    }

    public string Name { get; }
    public ObservableCollection<PromptEditorItemViewModel> Prompts { get; }
}

public sealed class PromptNavigationItemViewModel
{
    public PromptNavigationItemViewModel(
        string categoryName,
        PromptEditorItemViewModel prompt)
    {
        CategoryName = categoryName;
        Prompt = prompt;
    }

    public string CategoryName { get; }
    public PromptEditorItemViewModel Prompt { get; }
}

public sealed class PromptEditorItemViewModel : ViewModelBase
{
    private readonly Action _changed;
    private string _text = string.Empty;
    private bool _isLoading;

    public PromptEditorItemViewModel(
        GlobalPromptKey key,
        string label,
        string description,
        Action changed)
    {
        Key = key;
        Label = label;
        Description = description;
        _changed = changed;
    }

    public GlobalPromptKey Key { get; }
    public string Label { get; }
    public string Description { get; }
    public string LocalOverrideHint =>
        Key switch
        {
            GlobalPromptKey.ChatSystem =>
                "角色局部入口：打开个人聊天 → 角色提示词。那里直接修改角色卡的 system_prompt 与历史后指令，不创建第二份副本。",
            GlobalPromptKey.MemoryUpdateSystem
                or GlobalPromptKey.MemoryCompressionSystem =>
                "唯一生效来源：每项记忆功能只有这一份可编辑全局提示词；运行资料由程序附加，不存在第二份 User 模板或局部版本。",
            GlobalPromptKey.GroupRelaySystem =>
                "局部补充入口：打开群聊 → 群聊 → 当前群聊的接力提示词（高级）。只影响该群聊的角色接力。",
            GlobalPromptKey.GroupMemoryMergeSystem =>
                "唯一生效来源：群聊记忆合并只有这一份可编辑全局提示词；运行资料由程序附加，不存在第二份 User 模板或群聊局部版本。",
            GlobalPromptKey.CampaignGmSystem
                or GlobalPromptKey.CampaignPlayerSystem =>
                "本局专用内容来自跑团大厅与剧本结构；全局职责可从跑团大厅按钮直接返回这里修改。",
            _ =>
                "更具体的角色卡、预设与会话配置仍按现有优先级生效；这里不重复创建第二份聊天文本框。"
        };

    public string Text
    {
        get => _text;
        set
        {
            if (SetProperty(ref _text, value ?? string.Empty) && !_isLoading)
            {
                _changed();
            }
        }
    }

    public void Load(string value)
    {
        _isLoading = true;
        Text = value;
        _isLoading = false;
    }
}
