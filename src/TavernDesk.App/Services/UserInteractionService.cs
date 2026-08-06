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

public enum DataRootMigrationDecision
{
    Cancel,
    KeepTargetAsIs,
    CopyCurrentData
}

public sealed record GroupChatDraft(
    string Title,
    IReadOnlyList<string> CharacterIds);

public interface IUserInteractionService
{
    Task<string?> EditTextAsync(string title, string prompt, string initialText);
    Task<string?> PromptModelNameAsync(string initialText = "") =>
        EditTextAsync(
            "添加自定义模型",
            "输入模型 ID 或名称。保存后只写入本地模型目录，不会发起网络请求。",
            initialText);
    Task<string?> PromptRegenerationRequirementAsync() =>
        Task.FromResult<string?>(string.Empty);
    DeleteMessageDecision ConfirmMessageDeletion();
    UnsavedChangesDecision ConfirmUnsavedCharacterChanges(string characterName);
    UnsavedChangesDecision ConfirmUnsavedProviderChanges(string providerName);
    bool ConfirmCharacterDeletion(string characterName, int conversationCount);
    bool ConfirmShelfDeletion(string shelfName);
    bool ConfirmPresetDeletion(string presetName);
    bool ConfirmProviderDeletion(string providerName);
    bool ConfirmWorldbookDeletion(string worldbookName) => true;
    bool ConfirmCampaignDeletion(string campaignTitle, int eventCount) => false;
    bool ConfirmConversationDeletion(string conversationTitle) => false;
    bool ConfirmSecretClear(string providerName);
    DataRootMigrationDecision ConfirmDataRootMigration(
        string currentRoot,
        string newRoot) => DataRootMigrationDecision.Cancel;
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

    public Task<string?> PromptRegenerationRequirementAsync()
    {
        var dialog = new RegenerationRequirementDialog
        {
            Owner = Application.Current.MainWindow
        };
        return Task.FromResult(
            dialog.ShowDialog() == true
                ? dialog.ResultText
                : null);
    }

    public Task<string?> PromptModelNameAsync(string initialText = "")
    {
        var dialog = new CustomModelDialog(initialText)
        {
            Owner = Application.Current.MainWindow
        };
        return Task.FromResult(
            dialog.ShowDialog() == true
                ? dialog.ResultText
                : null);
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

    public bool ConfirmConversationDeletion(string conversationTitle) =>
        MessageBox.Show(
            Application.Current.MainWindow,
            $"永久删除聊天记录“{conversationTitle}”？\n\n"
            + "本会话的全部消息、候选回复和本地聊天缓存都会删除，删除后无法恢复。\n"
            + "角色卡、角色整体记忆和其他聊天记录不受影响。",
            "删除聊天记录",
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

    public bool ConfirmWorldbookDeletion(string worldbookName) =>
        MessageBox.Show(
            Application.Current.MainWindow,
            $"删除世界书“{worldbookName}”？\n\n已保存的原始 JSON 工作副本和其 Embedding 派生索引都会删除；用户原文件不会修改。",
            "删除世界书",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;

    public bool ConfirmCampaignDeletion(string campaignTitle, int eventCount) =>
        MessageBox.Show(
            Application.Current.MainWindow,
            $"永久删除跑团“{campaignTitle}”？\n\n"
            + $"该局的全部席位和 {eventCount} 条跑团记录会同时删除，删除后无法恢复。\n"
            + "剧本卡、角色卡和其他跑团不会被删除。",
            "确认永久删除跑团",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;

    public bool ConfirmSecretClear(string providerName) =>
        MessageBox.Show(
            Application.Current.MainWindow,
            $"清除“{providerName}”保存在 Windows DPAPI 中的 API Key？",
            "清除 API Key",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;

    public DataRootMigrationDecision ConfirmDataRootMigration(
        string currentRoot,
        string newRoot)
    {
        var result = MessageBox.Show(
            Application.Current.MainWindow,
            "个人资料目录即将切换。是否先把当前数据库、聊天记录、角色卡、剧本、附件和其他个人资料复制到新目录？\n\n"
            + "选择“是”会复制当前资料并保留旧目录作为安全备份；选择“否”只切换配置，不会覆盖新目录中的已有文件；选择“取消”保持不变。\n\n"
            + $"当前：{currentRoot}\n新目录：{newRoot}",
            "切换个人资料目录",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);
        return result switch
        {
            MessageBoxResult.Yes => DataRootMigrationDecision.CopyCurrentData,
            MessageBoxResult.No => DataRootMigrationDecision.KeepTargetAsIs,
            _ => DataRootMigrationDecision.Cancel
        };
    }

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
