using System.Collections.ObjectModel;
using System.Windows;
using TavernDesk.App.Localization;
using TavernDesk.App.Presentation;
using TavernDesk.App.Services;
using TavernDesk.Core.Models;

namespace TavernDesk.App;

public partial class GroupChatDialog : Window
{
    public GroupChatDialog(IReadOnlyList<Character> characters)
    {
        InitializeComponent();
        Choices = new ObservableCollection<GroupCharacterChoice>(
            characters
                .OrderBy(character => character.Name, StringComparer.OrdinalIgnoreCase)
                .Select(character => new GroupCharacterChoice(character)));
        DataContext = this;
    }

    public ObservableCollection<GroupCharacterChoice> Choices { get; }
    public GroupChatDraft? Result { get; private set; }

    private void Create_OnClick(object sender, RoutedEventArgs e)
    {
        var title = TitleBox.Text.Trim();
        var selectedIds = Choices
            .Where(choice => choice.IsSelected)
            .Select(choice => choice.Character.Id)
            .ToArray();
        if (title.Length == 0)
        {
            ValidationText.Text = LanguageRuntime.GetString("GroupChat.NameRequired");
            return;
        }

        if (selectedIds.Length < 2)
        {
            ValidationText.Text = LanguageRuntime.GetString("GroupChat.MinimumMembers");
            return;
        }

        Result = new GroupChatDraft(title, selectedIds);
        DialogResult = true;
    }
}

public sealed class GroupCharacterChoice : ViewModelBase
{
    private bool _isSelected;

    public GroupCharacterChoice(Character character)
    {
        Character = character;
        DescriptionPreview = BuildDescriptionPreview(character.Description);
    }

    public Character Character { get; }

    public string DescriptionPreview { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    private static string BuildDescriptionPreview(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return string.Empty;
        }

        var normalized = description
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var lines = new List<string>();
        foreach (var rawLine in normalized.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.Length > 120)
            {
                line = line[..120] + "…";
            }

            lines.Add(line);
            if (lines.Count == 2)
            {
                break;
            }
        }

        return string.Join("\n", lines);
    }
}
