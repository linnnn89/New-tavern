using System.IO;
using System.Text;
using Microsoft.Win32;
using TavernDesk.App.Localization;
using TavernDesk.Core.Models;

namespace TavernDesk.App.Services;

public interface IFileDialogService
{
    string? PickCharacterCard();
    string? PickCharacterAvatar();
    string? PickCampaignScenarioCard();
    string? PickCharacterCardExportPath(Character character);
    string? PickChatJsonl();
    string? PickChatJsonlExportPath(string conversationTitle);
    string? PickPromptProfileExportPath();
    string? PickWorldbookSource() => null;
    string? PickDataRoot() => null;
}

public sealed class FileDialogService : IFileDialogService
{
    public string? PickCharacterCard()
    {
        var dialog = new OpenFileDialog
        {
            Title = LanguageRuntime.GetString("FileDialog.ImportCharacter.Title"),
            Filter = LanguageRuntime.GetString("FileDialog.ImportCharacter.Filter"),
            CheckFileExists = true,
            Multiselect = false
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickCharacterAvatar()
    {
        var dialog = new OpenFileDialog
        {
            Title = LanguageRuntime.GetString("FileDialog.ReplaceCharacterImage.Title"),
            Filter = LanguageRuntime.GetString("FileDialog.Image.Filter"),
            CheckFileExists = true,
            Multiselect = false
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickCampaignScenarioCard()
    {
        var dialog = new OpenFileDialog
        {
            Title = LanguageRuntime.GetString("FileDialog.ImportScenario.Title"),
            Filter = LanguageRuntime.GetString("FileDialog.ImportScenario.Filter"),
            CheckFileExists = true,
            Multiselect = false
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickCharacterCardExportPath(Character character)
    {
        var extension = character.SourceCardFormat switch
        {
            CharacterCardFormat.Png => "png",
            CharacterCardFormat.Charx => "charx",
            _ => "json"
        };
        var filterIndex = character.SourceCardFormat switch
        {
            CharacterCardFormat.Png => 1,
            CharacterCardFormat.Json => 2,
            CharacterCardFormat.Charx => 3,
            _ => 1
        };
        var dialog = new SaveFileDialog
        {
            Title = LanguageRuntime.GetString("FileDialog.ExportCharacter.Title"),
            Filter = LanguageRuntime.GetString("FileDialog.ExportCharacter.Filter"),
            FilterIndex = filterIndex,
            DefaultExt = extension,
            AddExtension = true,
            OverwritePrompt = true,
            FileName = SanitizeFileName(character.Name)
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickChatJsonl()
    {
        var dialog = new OpenFileDialog
        {
            Title = LanguageRuntime.GetString("FileDialog.ImportChat.Title"),
            Filter = LanguageRuntime.GetString("FileDialog.ChatImport.Filter"),
            CheckFileExists = true,
            Multiselect = false
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickChatJsonlExportPath(string conversationTitle)
    {
        var dialog = new SaveFileDialog
        {
            Title = LanguageRuntime.GetString("FileDialog.ExportChat.Title"),
            Filter = LanguageRuntime.GetString("FileDialog.ChatExport.Filter"),
            DefaultExt = "jsonl",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = SanitizeFileName(conversationTitle)
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickPromptProfileExportPath()
    {
        var dialog = new SaveFileDialog
        {
            Title = LanguageRuntime.GetString("FileDialog.SavePrompts.Title"),
            Filter = LanguageRuntime.GetString("FileDialog.Prompts.Filter"),
            DefaultExt = "json",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = "taverndesk-prompts"
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickWorldbookSource()
    {
        var dialog = new OpenFileDialog
        {
            Title = LanguageRuntime.GetString("FileDialog.ImportWorldbook.Title"),
            Filter = LanguageRuntime.GetString("FileDialog.ImportWorldbook.Filter"),
            CheckFileExists = true,
            Multiselect = false
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickDataRoot()
    {
        var dialog = new OpenFolderDialog
        {
            Title = LanguageRuntime.GetString("FileDialog.SelectDataRoot.Title"),
            Multiselect = false,
            ValidateNames = true
        };

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim())
        {
            builder.Append(invalid.Contains(character) ? '_' : character);
        }

        return builder.Length == 0 ? "character-card" : builder.ToString();
    }
}
