using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using TavernDesk.App.Localization;
using TavernDesk.App.Presentation;
using TavernDesk.Core.Models;

namespace TavernDesk.App.ViewModels;

public sealed class CharacterEditBuffer : ViewModelBase
{
    private string _name = string.Empty;
    private string _description = string.Empty;
    private string _personality = string.Empty;
    private string _scenario = string.Empty;
    private string _firstMessage = string.Empty;
    private string _messageExample = string.Empty;
    private string _creatorNotes = string.Empty;
    private string _systemPrompt = string.Empty;
    private string _postHistoryInstructions = string.Empty;
    private string _tagsText = string.Empty;
    private string _creator = string.Empty;
    private string _characterVersion = string.Empty;
    private string _characterBookJson = "{}";
    private string _depthPrompt = string.Empty;
    private int _depthPromptDepth = 4;
    private string _depthPromptRole = "system";
    private string _rawCardJson = "{}";
    private bool _isDirty;
    private bool _loading;
    private int _changeVersion;

    public CharacterEditBuffer()
    {
        AddAlternateGreetingCommand = new RelayCommand(AddAlternateGreeting);
        RemoveAlternateGreetingCommand = new RelayCommand(
            parameter => RemoveAlternateGreeting(parameter as AlternateGreetingEditItem),
            parameter => parameter is AlternateGreetingEditItem);
    }

    public string CharacterId { get; private set; } = string.Empty;
    public ObservableCollection<AlternateGreetingEditItem> AlternateGreetings { get; } = [];
    public RelayCommand AddAlternateGreetingCommand { get; }
    public RelayCommand RemoveAlternateGreetingCommand { get; }

    public string Name
    {
        get => _name;
        set => SetEditable(ref _name, value);
    }

    public string Description
    {
        get => _description;
        set => SetEditable(ref _description, value);
    }

    public string Personality
    {
        get => _personality;
        set => SetEditable(ref _personality, value);
    }

    public string Scenario
    {
        get => _scenario;
        set => SetEditable(ref _scenario, value);
    }

    public string FirstMessage
    {
        get => _firstMessage;
        set => SetEditable(ref _firstMessage, value);
    }

    public string MessageExample
    {
        get => _messageExample;
        set => SetEditable(ref _messageExample, value);
    }

    public string CreatorNotes
    {
        get => _creatorNotes;
        set => SetEditable(ref _creatorNotes, value);
    }

    public string SystemPrompt
    {
        get => _systemPrompt;
        set => SetEditable(ref _systemPrompt, value);
    }

    public string PostHistoryInstructions
    {
        get => _postHistoryInstructions;
        set => SetEditable(ref _postHistoryInstructions, value);
    }

    public string AlternateGreetingsJson
    {
        get => FormatJson(BuildAlternateGreetingsArray());
        set
        {
            var array = ParseStringArray(
                value,
                LanguageRuntime.GetString("Validation.Character.AlternateGreetings"));
            ReplaceAlternateGreetings(array);
            OnPropertyChanged();
            if (!_loading)
            {
                IsDirty = true;
            }
        }
    }

    public string TagsText
    {
        get => _tagsText;
        set => SetEditable(ref _tagsText, value);
    }

    public string Creator
    {
        get => _creator;
        set => SetEditable(ref _creator, value);
    }

    public string CharacterVersion
    {
        get => _characterVersion;
        set => SetEditable(ref _characterVersion, value);
    }

    public string CharacterBookJson
    {
        get => _characterBookJson;
        set => SetEditable(ref _characterBookJson, value);
    }

    public string DepthPrompt
    {
        get => _depthPrompt;
        set => SetEditable(ref _depthPrompt, value);
    }

    public int DepthPromptDepth
    {
        get => _depthPromptDepth;
        set => SetEditable(ref _depthPromptDepth, value);
    }

    public string DepthPromptRole
    {
        get => _depthPromptRole;
        set => SetEditable(ref _depthPromptRole, value);
    }

    public string RawCardJson
    {
        get => _rawCardJson;
        private set => SetEditable(ref _rawCardJson, value);
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set => SetProperty(ref _isDirty, value);
    }

    public int ChangeVersion => _changeVersion;

    public void Load(Character character)
    {
        _loading = true;
        try
        {
            CharacterId = character.Id;
            Name = character.Name;
            Description = character.Description;
            Personality = character.Personality;
            Scenario = character.Scenario;
            FirstMessage = character.FirstMessage;
            LoadAdvancedFields(ParseRoot(character.RawCardJson));
            RawCardJson = FormatJson(ParseRoot(character.RawCardJson));
            IsDirty = false;
            _changeVersion++;
            OnPropertyChanged(nameof(CharacterId));
            OnPropertyChanged(nameof(ChangeVersion));
        }
        finally
        {
            _loading = false;
        }
    }

    public void Clear()
    {
        Load(new Character());
        CharacterId = string.Empty;
        OnPropertyChanged(nameof(CharacterId));
    }

    public void ApplyTo(Character character)
    {
        if (!string.Equals(character.Id, CharacterId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                LanguageRuntime.GetString("Validation.Character.Mismatch"));
        }

        var root = ParseRoot(RawCardJson);
        var data = GetDataObject(root);
        var name = Name.Trim();
        var alternateGreetings = BuildAlternateGreetingsArray();
        var characterBook = ParseObject(
            CharacterBookJson,
            LanguageRuntime.GetString("Validation.Character.CharacterBook"));
        string? depthPromptRole = null;
        if (!string.IsNullOrWhiteSpace(DepthPrompt))
        {
            if (DepthPromptDepth is < 1 or > 100)
            {
                throw new InvalidDataException(
                    LanguageRuntime.GetString("Validation.Character.DepthRange"));
            }

            var role = DepthPromptRole.Trim().ToLowerInvariant();
            if (role is not ("system" or "user" or "assistant"))
            {
                throw new InvalidDataException(
                    LanguageRuntime.GetString("Validation.Character.DepthRole"));
            }

            depthPromptRole = role;
        }

        data["name"] = name;
        data["description"] = Description;
        data["personality"] = Personality;
        data["scenario"] = Scenario;
        data["first_mes"] = FirstMessage;
        data["mes_example"] = MessageExample;
        data["creator_notes"] = CreatorNotes;
        data["system_prompt"] = SystemPrompt;
        data["post_history_instructions"] = PostHistoryInstructions;
        data["alternate_greetings"] = alternateGreetings;
        data["tags"] = new JsonArray(ParseTags()
            .Select(tag => (JsonNode?)JsonValue.Create(tag))
            .ToArray());
        data["creator"] = Creator;
        data["character_version"] = CharacterVersion;
        data["character_book"] = characterBook;
        var extensions = data["extensions"] as JsonObject;
        if (extensions is null)
        {
            extensions = new JsonObject();
            data["extensions"] = extensions;
        }

        if (depthPromptRole is null)
        {
            extensions.Remove("depth_prompt");
        }
        else
        {
            var depthPrompt = extensions["depth_prompt"] as JsonObject
                ?? new JsonObject();
            depthPrompt["prompt"] = DepthPrompt;
            depthPrompt["depth"] = DepthPromptDepth;
            depthPrompt["role"] = depthPromptRole;
            extensions["depth_prompt"] = depthPrompt;
        }

        var updatedRawJson = FormatJson(root);
        character.Name = name;
        character.Description = Description;
        character.Personality = Personality;
        character.Scenario = Scenario;
        character.FirstMessage = FirstMessage;
        character.RawCardJson = updatedRawJson;
        _loading = true;
        try
        {
            RawCardJson = character.RawCardJson;
        }
        finally
        {
            _loading = false;
        }
    }

    public void ReplaceRawJson(string rawJson)
    {
        var root = ParseRoot(rawJson);
        _loading = true;
        try
        {
            var data = GetDataObject(root);
            Name = ReadString(data, "name");
            Description = ReadString(data, "description");
            Personality = ReadString(data, "personality");
            Scenario = ReadString(data, "scenario");
            FirstMessage = ReadString(data, "first_mes");
            LoadAdvancedFields(root);
            RawCardJson = FormatJson(root);
        }
        finally
        {
            _loading = false;
        }

        MarkDirty();
    }

    public bool MarkSaved(int expectedChangeVersion)
    {
        if (_changeVersion != expectedChangeVersion)
        {
            return false;
        }

        IsDirty = false;
        return true;
    }

    private void SetEditable(ref string field, string value)
    {
        if (SetProperty(ref field, value) && !_loading)
        {
            MarkDirty();
        }
    }

    private void SetEditable(ref int field, int value)
    {
        if (SetProperty(ref field, value) && !_loading)
        {
            MarkDirty();
        }
    }

    private void MarkDirty()
    {
        _changeVersion++;
        OnPropertyChanged(nameof(ChangeVersion));
        IsDirty = true;
    }

    private void LoadAdvancedFields(JsonObject root)
    {
        var data = GetDataObject(root);
        MessageExample = ReadString(data, "mes_example");
        CreatorNotes = ReadString(data, "creator_notes");
        SystemPrompt = ReadString(data, "system_prompt");
        PostHistoryInstructions = ReadString(data, "post_history_instructions");
        AlternateGreetingsJson = data["alternate_greetings"] is JsonArray greetings
            ? FormatJson(greetings)
            : "[]";
        TagsText = data["tags"] is JsonArray tags
            ? string.Join(
                ", ",
                tags.OfType<JsonValue>()
                    .Select(value => value.TryGetValue<string>(out var tag)
                        ? tag
                        : string.Empty)
                    .Where(tag => tag.Length > 0))
            : string.Empty;
        Creator = ReadString(data, "creator");
        CharacterVersion = ReadString(data, "character_version");
        CharacterBookJson = data["character_book"] is JsonObject book
            ? FormatJson(book)
            : "{}";
        var depthPrompt = (data["extensions"] as JsonObject)?["depth_prompt"]
            as JsonObject;
        DepthPrompt = depthPrompt is null
            ? string.Empty
            : ReadString(depthPrompt, "prompt");
        DepthPromptDepth = ReadInt32(depthPrompt, "depth") ?? 4;
        DepthPromptRole = ReadString(depthPrompt, "role") is { Length: > 0 } role
            ? role
            : "system";
    }

    private void AddAlternateGreeting()
    {
        AlternateGreetings.Add(new AlternateGreetingEditItem(
            string.Empty,
            OnAlternateGreetingChanged));
        OnAlternateGreetingChanged();
    }

    private void RemoveAlternateGreeting(AlternateGreetingEditItem? greeting)
    {
        if (greeting is null || !AlternateGreetings.Remove(greeting))
        {
            return;
        }

        OnAlternateGreetingChanged();
    }

    private void ReplaceAlternateGreetings(JsonArray greetings)
    {
        var values = greetings
            .Select(node =>
            {
                if (node is JsonValue value
                    && value.TryGetValue<string>(out var text))
                {
                    return text;
                }

                throw new InvalidDataException(
                    LanguageRuntime.GetString("Validation.Character.StringArrayOnly"));
            })
            .ToArray();

        AlternateGreetings.Clear();
        foreach (var value in values)
        {
            AlternateGreetings.Add(new AlternateGreetingEditItem(
                value,
                OnAlternateGreetingChanged));
        }
    }

    private JsonArray BuildAlternateGreetingsArray() =>
        new(AlternateGreetings
            .Select(item => (JsonNode?)JsonValue.Create(item.Text))
            .ToArray());

    private void OnAlternateGreetingChanged()
    {
        OnPropertyChanged(nameof(AlternateGreetingsJson));
        if (!_loading)
        {
            MarkDirty();
        }
    }

    private IEnumerable<string> ParseTags() =>
        TagsText
            .Split([',', '，', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(tag => tag.Trim())
            .Where(tag => tag.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static JsonObject ParseRoot(string rawJson)
    {
        try
        {
            var root = JsonNode.Parse(
                string.IsNullOrWhiteSpace(rawJson) ? "{}" : rawJson,
                documentOptions: new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                    MaxDepth = 256
                }) as JsonObject
                ?? throw new InvalidDataException(
                    LanguageRuntime.GetString("Validation.Character.RootObject"));
            _ = GetDataObject(root);
            return root;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                LanguageRuntime.Format(
                    "Validation.Character.ParseFailedFormat",
                    LanguageRuntime.ErrorMessage(exception)),
                exception);
        }
    }

    private static JsonObject GetDataObject(JsonObject root)
    {
        var spec = ReadString(root, "spec");
        if (spec is "chara_card_v2" or "chara_card_v3")
        {
            return root["data"] as JsonObject
                   ?? throw new InvalidDataException(
                       LanguageRuntime.Format(
                           "Validation.Character.DataObjectRequiredFormat",
                           spec));
        }

        return root["data"] as JsonObject ?? root;
    }

    private static JsonArray ParseStringArray(string json, string label)
    {
        try
        {
            var array = JsonNode.Parse(
                string.IsNullOrWhiteSpace(json) ? "[]" : json)
                as JsonArray
                ?? throw new InvalidDataException(
                    LanguageRuntime.Format("Validation.Json.ArrayRequiredFormat", label));
            if (array.Any(node =>
                    node is not JsonValue value
                    || !value.TryGetValue<string>(out _)))
            {
                throw new InvalidDataException(
                    LanguageRuntime.Format("Validation.Json.StringArrayOnlyFormat", label));
            }

            return (JsonArray)array.DeepClone();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                LanguageRuntime.Format(
                    "Validation.Json.ParseFailedFormat",
                    label,
                    LanguageRuntime.ErrorMessage(exception)),
                exception);
        }
    }

    private static JsonObject ParseObject(string json, string label)
    {
        try
        {
            return JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json)
                       as JsonObject
                   ?? throw new InvalidDataException(
                       LanguageRuntime.Format("Validation.Json.ObjectRequiredFormat", label));
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                LanguageRuntime.Format(
                    "Validation.Json.ParseFailedFormat",
                    label,
                    LanguageRuntime.ErrorMessage(exception)),
                exception);
        }
    }

    private static string ReadString(JsonObject? source, string propertyName) =>
        source?[propertyName] is JsonValue value
        && value.TryGetValue<string>(out var result)
            ? result
            : string.Empty;

    private static int? ReadInt32(JsonObject? source, string propertyName) =>
        source?[propertyName] is JsonValue value
        && value.TryGetValue<int>(out var result)
            ? result
            : null;

    private static string FormatJson(JsonNode node) =>
        node.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true
        });
}

public sealed class AlternateGreetingEditItem : ViewModelBase
{
    private readonly Action _changed;
    private string _text;

    public AlternateGreetingEditItem(string text, Action changed)
    {
        _text = text;
        _changed = changed;
    }

    public string Text
    {
        get => _text;
        set
        {
            if (SetProperty(ref _text, value))
            {
                _changed();
            }
        }
    }
}
