using System.Collections.ObjectModel;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using TavernDesk.App.Localization;
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
        LanguageRuntime.GetString("PromptSettings.Status.Intro");

    public PromptSettingsViewModel(
        IGlobalPromptConfiguration configuration,
        IFileDialogService fileDialog)
    {
        _configuration = configuration;
        _fileDialog = fileDialog;
        Categories =
        [
            new(
                LanguageRuntime.GetString("PromptSettings.Category.Chat"),
                [
                    Create(
                        GlobalPromptKey.ChatSystem,
                        LanguageRuntime.GetString("PromptSettings.ChatSystem.Label"),
                        LanguageRuntime.GetString("PromptSettings.ChatSystem.Description"))
                ]),
            new(
                LanguageRuntime.GetString("PromptSettings.Category.Memory"),
                [
                    Create(
                        GlobalPromptKey.MemoryUpdateSystem,
                        LanguageRuntime.GetString("PromptSettings.MemoryUpdate.Label"),
                        LanguageRuntime.GetString("PromptSettings.MemoryUpdate.Description")),
                    Create(
                        GlobalPromptKey.MemoryCompressionSystem,
                        LanguageRuntime.GetString("PromptSettings.MemoryCompression.Label"),
                        LanguageRuntime.GetString("PromptSettings.MemoryCompression.Description"))
                ]),
            new(
                LanguageRuntime.GetString("PromptSettings.Category.GroupChat"),
                [
                    Create(
                        GlobalPromptKey.GroupRelaySystem,
                        LanguageRuntime.GetString("PromptSettings.GroupRelay.Label"),
                        LanguageRuntime.GetString("PromptSettings.GroupRelay.Description")),
                    Create(
                        GlobalPromptKey.GroupMemoryMergeSystem,
                        LanguageRuntime.GetString("PromptSettings.GroupMemory.Label"),
                        LanguageRuntime.GetString("PromptSettings.GroupMemory.Description"))
                ]),
            new(
                LanguageRuntime.GetString("PromptSettings.Category.Campaign"),
                [
                    Create(
                        GlobalPromptKey.CampaignGmSystem,
                        LanguageRuntime.GetString("PromptSettings.CampaignGm.Label"),
                        LanguageRuntime.GetString("PromptSettings.CampaignGm.Description")),
                    Create(
                        GlobalPromptKey.CampaignPlayerSystem,
                        LanguageRuntime.GetString("PromptSettings.CampaignPlayer.Label"),
                        LanguageRuntime.GetString("PromptSettings.CampaignPlayer.Description"))
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

        Status = LanguageRuntime.GetString("PromptSettings.Saved");
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
        Status = LanguageRuntime.Format(
            "PromptSettings.ExportedFormat",
            Path.GetFileName(path));
    }

    private void RestoreSelectedDefault()
    {
        if (SelectedPrompt is null)
        {
            return;
        }

        SelectedPrompt.Text = GlobalPromptDefaults.Get(SelectedPrompt.Key);
        Status = LanguageRuntime.Format(
            "PromptSettings.RestoredFormat",
            SelectedPrompt.Label);
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
                LanguageRuntime.GetString("PromptSettings.OverrideHint.Chat"),
            GlobalPromptKey.MemoryUpdateSystem
                or GlobalPromptKey.MemoryCompressionSystem =>
                LanguageRuntime.GetString("PromptSettings.OverrideHint.Memory"),
            GlobalPromptKey.GroupRelaySystem =>
                LanguageRuntime.GetString("PromptSettings.OverrideHint.GroupRelay"),
            GlobalPromptKey.GroupMemoryMergeSystem =>
                LanguageRuntime.GetString("PromptSettings.OverrideHint.GroupMemory"),
            GlobalPromptKey.CampaignGmSystem
                or GlobalPromptKey.CampaignPlayerSystem =>
                LanguageRuntime.GetString("PromptSettings.OverrideHint.Campaign"),
            _ =>
                LanguageRuntime.GetString("PromptSettings.OverrideHint.Other")
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
