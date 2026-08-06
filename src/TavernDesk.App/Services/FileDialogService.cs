using System.IO;
using System.Text;
using Microsoft.Win32;
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
            Title = "导入角色卡",
            Filter = "角色卡 (*.png;*.json;*.charx)|*.png;*.json;*.charx|PNG 角色卡 (*.png)|*.png|JSON 角色卡 (*.json)|*.json|CHARX 角色卡 (*.charx)|*.charx|全部文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickCharacterAvatar()
    {
        var dialog = new OpenFileDialog
        {
            Title = "替换角色图片",
            Filter = "图片 (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif|全部文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickCampaignScenarioCard()
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入跑团剧本卡",
            Filter = "PNG 剧本卡 (*.png)|*.png|全部文件 (*.*)|*.*",
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
            Title = "导出角色卡",
            Filter = "PNG 角色卡 (*.png)|*.png|JSON 角色卡 (*.json)|*.json|CHARX 角色卡 (*.charx)|*.charx",
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
            Title = "导入聊天记录",
            Filter = "SillyTavern 聊天记录 (*.jsonl)|*.jsonl|全部文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickChatJsonlExportPath(string conversationTitle)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出当前聊天记录",
            Filter = "SillyTavern 聊天记录 (*.jsonl)|*.jsonl",
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
            Title = "另存当前全局提示词配置",
            Filter = "TavernDesk 提示词配置 (*.json)|*.json",
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
            Title = "导入世界书或包含内置世界书的角色卡",
            Filter = "世界书/角色卡 (*.json;*.png;*.charx)|*.json;*.png;*.charx|世界书 JSON (*.json)|*.json|PNG 角色卡 (*.png)|*.png|CHARX 角色卡 (*.charx)|*.charx|全部文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickDataRoot()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择 TavernDesk 个人资料目录",
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
