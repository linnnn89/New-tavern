using System.Windows;
using TavernDesk.Core.Models;

namespace TavernDesk.App.Services;

public enum DeleteMessageDecision
{
    Cancel,
    SelectedOnly,
    SelectedAndFollowing
}

public enum UnsavedChangesDecision
{
    Cancel,
    Discard,
    Save
}

public sealed record GroupChatDraft(
    string Title,
    IReadOnlyList<string> CharacterIds);

public interface IUserInteractionService
{
    Task<string?> EditTextAsync(string title, string prompt, string initialText);
    DeleteMessageDecision ConfirmMessageDeletion();
    UnsavedChangesDecision ConfirmUnsavedCharacterChanges(string characterName);
    UnsavedChangesDecision ConfirmUnsavedProviderChanges(string providerName);
    bool ConfirmCharacterDeletion(string characterName, int conversationCount);
    bool ConfirmShelfDeletion(string shelfName);
    bool ConfirmPresetDeletion(string presetName);
    bool ConfirmProviderDeletion(string providerName);
    bool ConfirmSecretClear(string providerName);
    Task<GroupChatDraft?> CreateGroupChatAsync(IReadOnlyList<Character> characters);
    void CopyText(string text);
}

public sealed class UserInteractionService : IUserInteractionService
{
    private readonly WindowPlacementService _windowPlacement;

    public UserInteractionService(WindowPlacementService windowPlacement)
    {
        _windowPlacement = windowPlacement;
    }

    public async Task<string?> EditTextAsync(string title, string prompt, string initialText)
    {
        var dialog = new TextEditorDialog(title, prompt, initialText)
        {
            Owner = Application.Current.MainWindow
        };
        await _windowPlacement.RestoreAsync(dialog, "window.textEditor", 760, 580);
        var accepted = dialog.ShowDialog() == true;
        await _windowPlacement.SaveAsync(dialog, "window.textEditor");
        return accepted ? dialog.ResultText : null;
    }

    public DeleteMessageDecision ConfirmMessageDeletion()
    {
        var range = MessageBox.Show(
            Application.Current.MainWindow,
            "选择删除范围：\n\n“是”＝永久删除当前消息及其后的全部消息\n“否”＝只永久删除当前消息\n“取消”＝不删除",
            "删除范围",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);
        if (range == MessageBoxResult.Cancel)
        {
            return DeleteMessageDecision.Cancel;
        }

        var decision = range == MessageBoxResult.Yes
            ? DeleteMessageDecision.SelectedAndFollowing
            : DeleteMessageDecision.SelectedOnly;

        var final = MessageBox.Show(
            Application.Current.MainWindow,
            decision == DeleteMessageDecision.SelectedAndFollowing
                ? "确认永久删除当前消息及其后的全部消息？\n\n删除后无法恢复。"
                : "确认永久删除当前消息？\n\n删除后无法恢复。",
            "确认永久删除",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        return final == MessageBoxResult.Yes
            ? decision
            : DeleteMessageDecision.Cancel;
    }

    public UnsavedChangesDecision ConfirmUnsavedCharacterChanges(string characterName)
    {
        var result = MessageBox.Show(
            Application.Current.MainWindow,
            $"“{characterName}”的角色设定尚未保存。\n\n选择“是”保存后继续，“否”放弃修改，“取消”留在当前界面。",
            "未保存的角色设定",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);
        return result switch
        {
            MessageBoxResult.Yes => UnsavedChangesDecision.Save,
            MessageBoxResult.No => UnsavedChangesDecision.Discard,
            _ => UnsavedChangesDecision.Cancel
        };
    }

    public UnsavedChangesDecision ConfirmUnsavedProviderChanges(string providerName)
    {
        var result = MessageBox.Show(
            Application.Current.MainWindow,
            $"“{providerName}”的接入商设置或待保存 API Key 尚未保存。\n\n选择“是”保存后继续，“否”放弃修改，“取消”留在当前界面。",
            "未保存的模型设置",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);
        return result switch
        {
            MessageBoxResult.Yes => UnsavedChangesDecision.Save,
            MessageBoxResult.No => UnsavedChangesDecision.Discard,
            _ => UnsavedChangesDecision.Cancel
        };
    }

    public bool ConfirmCharacterDeletion(string characterName, int conversationCount) =>
        MessageBox.Show(
            Application.Current.MainWindow,
            $"从角色书架删除“{characterName}”？\n\n"
            + (conversationCount == 0
                ? "该角色目前没有聊天记录。"
                : $"与该角色绑定的 {conversationCount} 条聊天会被保留，并显示在“已删除角色”下。")
            + "\n角色卡的导入工作副本会保留，原始来源文件不会改动。",
            "删除角色卡",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;

    public bool ConfirmShelfDeletion(string shelfName) =>
        MessageBox.Show(
            Application.Current.MainWindow,
            $"删除书架“{shelfName}”？\n\n角色卡本身不会被删除。",
            "删除自定义书架",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;

    public bool ConfirmPresetDeletion(string presetName) =>
        MessageBox.Show(
            Application.Current.MainWindow,
            $"删除预设“{presetName}”？\n\n它在全局、角色和会话中的挂载也会一起移除。",
            "删除预设",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;

    public bool ConfirmProviderDeletion(string providerName) =>
        MessageBox.Show(
            Application.Current.MainWindow,
            $"删除接入商“{providerName}”？\n\n其模型目录、功能分配和 TavernDesk 本地保存的 DPAPI Key 都会永久清除。此操作不能撤销。",
            "删除接入商",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;

    public bool ConfirmSecretClear(string providerName) =>
        MessageBox.Show(
            Application.Current.MainWindow,
            $"清除“{providerName}”保存在 Windows DPAPI 中的 API Key？",
            "清除 API Key",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;

    public async Task<GroupChatDraft?> CreateGroupChatAsync(
        IReadOnlyList<Character> characters)
    {
        var dialog = new GroupChatDialog(characters)
        {
            Owner = Application.Current.MainWindow
        };
        await _windowPlacement.RestoreAsync(dialog, "window.groupChatEditor", 640, 680);
        var accepted = dialog.ShowDialog() == true;
        await _windowPlacement.SaveAsync(dialog, "window.groupChatEditor");
        return accepted ? dialog.Result : null;
    }

    public void CopyText(string text)
    {
        Clipboard.SetText(text);
    }
}
