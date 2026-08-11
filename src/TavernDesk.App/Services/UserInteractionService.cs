using System.Windows;
using TavernDesk.App.Localization;
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
    void ShowWarning(string title, string message)
    {
        MessageBox.Show(
            Application.Current?.MainWindow,
            message,
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    Task<string?> EditTextAsync(string title, string prompt, string initialText);
    Task<string?> PromptModelNameAsync(string initialText = "") =>
        EditTextAsync(
            LanguageRuntime.GetString("Interaction.CustomModel.Title"),
            LanguageRuntime.GetString("Interaction.CustomModel.Prompt"),
            initialText);
    Task<string?> PromptRegenerationRequirementAsync() =>
        Task.FromResult<string?>(string.Empty);
    DeleteMessageDecision ConfirmMessageDeletion();
    UnsavedChangesDecision ConfirmUnsavedCharacterChanges(string characterName);
    UnsavedChangesDecision ConfirmUnsavedProviderChanges(string providerName);
    UnsavedChangesDecision ConfirmUnsavedCampaignLobby(string campaignTitle) =>
        UnsavedChangesDecision.Discard;
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

    public void ShowWarning(string title, string message) =>
        MessageBox.Show(
            Application.Current?.MainWindow,
            message,
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

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
            LanguageRuntime.GetString("Interaction.DeleteMessageRange.Message"),
            LanguageRuntime.GetString("Interaction.DeleteMessageRange.Title"),
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
                ? LanguageRuntime.GetString("Interaction.DeleteMessage.ConfirmTail")
                : LanguageRuntime.GetString("Interaction.DeleteMessage.ConfirmSingle"),
            LanguageRuntime.GetString("Interaction.DeleteMessage.Title"),
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
            LanguageRuntime.Format("Interaction.UnsavedCharacter.MessageFormat", characterName),
            LanguageRuntime.GetString("Interaction.UnsavedCharacter.Title"),
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
            LanguageRuntime.Format("Interaction.UnsavedProvider.MessageFormat", providerName),
            LanguageRuntime.GetString("Interaction.UnsavedProvider.Title"),
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);
        return result switch
        {
            MessageBoxResult.Yes => UnsavedChangesDecision.Save,
            MessageBoxResult.No => UnsavedChangesDecision.Discard,
            _ => UnsavedChangesDecision.Cancel
        };
    }

    public UnsavedChangesDecision ConfirmUnsavedCampaignLobby(
        string campaignTitle)
    {
        var result = MessageBox.Show(
            Application.Current.MainWindow,
            LanguageRuntime.Format("Interaction.UnstartedCampaign.MessageFormat", campaignTitle),
            LanguageRuntime.GetString("Interaction.UnstartedCampaign.Title"),
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question,
            MessageBoxResult.No);
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
            LanguageRuntime.Format(
                "Interaction.DeleteCharacter.MessageFormat",
                characterName,
                conversationCount == 0
                    ? LanguageRuntime.GetString("Interaction.DeleteCharacter.NoChats")
                    : LanguageRuntime.Format("Interaction.DeleteCharacter.ChatCountFormat", conversationCount)),
            LanguageRuntime.GetString("Interaction.DeleteCharacter.Title"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;

    public bool ConfirmConversationDeletion(string conversationTitle) =>
        MessageBox.Show(
            Application.Current.MainWindow,
            LanguageRuntime.Format("Interaction.DeleteConversation.MessageFormat", conversationTitle),
            LanguageRuntime.GetString("Interaction.DeleteConversation.Title"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;

    public bool ConfirmShelfDeletion(string shelfName) =>
        MessageBox.Show(
            Application.Current.MainWindow,
            LanguageRuntime.Format("Interaction.DeleteShelf.MessageFormat", shelfName),
            LanguageRuntime.GetString("Interaction.DeleteShelf.Title"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;

    public bool ConfirmPresetDeletion(string presetName) =>
        MessageBox.Show(
            Application.Current.MainWindow,
            LanguageRuntime.Format("Interaction.DeletePreset.MessageFormat", presetName),
            LanguageRuntime.GetString("Interaction.DeletePreset.Title"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;

    public bool ConfirmProviderDeletion(string providerName) =>
        MessageBox.Show(
            Application.Current.MainWindow,
            LanguageRuntime.Format("Interaction.DeleteProvider.MessageFormat", providerName),
            LanguageRuntime.GetString("Interaction.DeleteProvider.Title"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;

    public bool ConfirmWorldbookDeletion(string worldbookName) =>
        MessageBox.Show(
            Application.Current.MainWindow,
            LanguageRuntime.Format("Interaction.DeleteWorldbook.MessageFormat", worldbookName),
            LanguageRuntime.GetString("Interaction.DeleteWorldbook.Title"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;

    public bool ConfirmCampaignDeletion(string campaignTitle, int eventCount) =>
        MessageBox.Show(
            Application.Current.MainWindow,
            LanguageRuntime.Format("Interaction.DeleteCampaign.MessageFormat", campaignTitle, eventCount),
            LanguageRuntime.GetString("Interaction.DeleteCampaign.Title"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;

    public bool ConfirmSecretClear(string providerName) =>
        MessageBox.Show(
            Application.Current.MainWindow,
            LanguageRuntime.Format("Interaction.ClearKey.MessageFormat", providerName),
            LanguageRuntime.GetString("Interaction.ClearKey.Title"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;

    public DataRootMigrationDecision ConfirmDataRootMigration(
        string currentRoot,
        string newRoot)
    {
        var result = MessageBox.Show(
            Application.Current.MainWindow,
            LanguageRuntime.Format("Interaction.ChangeDataRoot.MessageFormat", currentRoot, newRoot),
            LanguageRuntime.GetString("Interaction.ChangeDataRoot.Title"),
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
