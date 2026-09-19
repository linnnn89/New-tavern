using System.Windows;
using TavernDesk.App;
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
    bool? ConfirmScenarioRecovery(CampaignScenarioEditDraft draft) => null;
    bool ConfirmInterfaceScale(int percent) => false;
    void ShowWarning(string title, string message)
    {
        LocalizedMessageBox.Show(
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
    bool ConfirmClearApiTestOutput(string outputDirectory) => false;
    Task<GroupChatDraft?> CreateGroupChatAsync(IReadOnlyList<Character> characters);
    void CopyText(string text);
}

public sealed class UserInteractionService : IUserInteractionService
{
    public bool? ConfirmScenarioRecovery(CampaignScenarioEditDraft draft)
    {
        var dialog = new SafeChoiceDialog(LanguageRuntime.GetString("Recovery.Title"),
            LanguageRuntime.Format("Recovery.Prompt", draft.Scenario.Title, draft.SavedAt.ToLocalTime()),
            LanguageRuntime.GetString("Recovery.Accept"), LanguageRuntime.GetString("Recovery.Discard"))
            { Owner = Application.Current.MainWindow };
        // Closing the title bar preserves the draft; only the explicit discard button deletes it.
        dialog.ShowDialog();
        return dialog.Choice;
    }

    public bool ConfirmInterfaceScale(int percent) => new SafeChoiceDialog(
        LanguageRuntime.GetString("ScaleConfirm.Title"), LanguageRuntime.Format("ScaleConfirm.Prompt", percent),
        LanguageRuntime.GetString("ScaleConfirm.Accept"), LanguageRuntime.GetString("ScaleConfirm.Revert"), timed: true)
        { Owner = Application.Current.MainWindow }.ShowDialog() == true;
    private readonly WindowPlacementService _windowPlacement;

    public UserInteractionService(WindowPlacementService windowPlacement)
    {
        _windowPlacement = windowPlacement;
    }

    public void ShowWarning(string title, string message) =>
        LocalizedMessageBox.Show(
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
        var actions = new[]
        {
            new DialogAction(
                LanguageRuntime.GetString("Interaction.DeleteMessage.SelectedAndFollowing"),
                MessageBoxResult.Yes,
                DialogButtonRole.Destructive),
            new DialogAction(
                LanguageRuntime.GetString("Interaction.DeleteMessage.SelectedOnly"),
                MessageBoxResult.No,
                DialogButtonRole.Secondary),
            new DialogAction(
                LanguageRuntime.GetString("Common.Cancel"),
                MessageBoxResult.Cancel,
                DialogButtonRole.Secondary,
                IsDefault: true,
                IsCancel: true)
        };
        var result = LocalizedMessageBox.Show(
            Application.Current.MainWindow,
            LanguageRuntime.GetString("Interaction.DeleteMessageRange.Message"),
            LanguageRuntime.GetString("Interaction.DeleteMessageRange.Title"),
            actions,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);

        return result switch
        {
            MessageBoxResult.Yes => DeleteMessageDecision.SelectedAndFollowing,
            MessageBoxResult.No => DeleteMessageDecision.SelectedOnly,
            _ => DeleteMessageDecision.Cancel
        };
    }

    private static UnsavedChangesDecision ConfirmUnsavedChangesDialog(
        string title,
        string message)
    {
        var actions = new[]
        {
            new DialogAction(
                LanguageRuntime.GetString("Interaction.Action.Save"),
                MessageBoxResult.Yes,
                DialogButtonRole.Primary,
                IsDefault: true),
            new DialogAction(
                LanguageRuntime.GetString("Interaction.Action.Discard"),
                MessageBoxResult.No,
                DialogButtonRole.Destructive),
            new DialogAction(
                LanguageRuntime.GetString("Common.Cancel"),
                MessageBoxResult.Cancel,
                DialogButtonRole.Secondary,
                IsCancel: true)
        };
        var result = LocalizedMessageBox.Show(
            Application.Current.MainWindow,
            message,
            title,
            actions,
            MessageBoxImage.Question,
            MessageBoxResult.Cancel);

        return result switch
        {
            MessageBoxResult.Yes => UnsavedChangesDecision.Save,
            MessageBoxResult.No => UnsavedChangesDecision.Discard,
            _ => UnsavedChangesDecision.Cancel
        };
    }

    private static bool ConfirmDestructiveAction(
        string title,
        string message,
        string destructiveActionLabel)
    {
        var actions = new[]
        {
            new DialogAction(
                destructiveActionLabel,
                MessageBoxResult.Yes,
                DialogButtonRole.Destructive),
            new DialogAction(
                LanguageRuntime.GetString("Common.Cancel"),
                MessageBoxResult.No,
                DialogButtonRole.Secondary,
                IsDefault: true,
                IsCancel: true)
        };
        return LocalizedMessageBox.Show(
            Application.Current.MainWindow,
            message,
            title,
            actions,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    public UnsavedChangesDecision ConfirmUnsavedCharacterChanges(string characterName) =>
        ConfirmUnsavedChangesDialog(
            LanguageRuntime.GetString("Interaction.UnsavedCharacter.Title"),
            LanguageRuntime.Format("Interaction.UnsavedCharacter.MessageFormat", characterName));

    public UnsavedChangesDecision ConfirmUnsavedProviderChanges(string providerName) =>
        ConfirmUnsavedChangesDialog(
            LanguageRuntime.GetString("Interaction.UnsavedProvider.Title"),
            LanguageRuntime.Format("Interaction.UnsavedProvider.MessageFormat", providerName));

    public UnsavedChangesDecision ConfirmUnsavedCampaignLobby(string campaignTitle) =>
        ConfirmUnsavedChangesDialog(
            LanguageRuntime.GetString("Interaction.UnstartedCampaign.Title"),
            LanguageRuntime.Format("Interaction.UnstartedCampaign.MessageFormat", campaignTitle));

    public bool ConfirmCharacterDeletion(string characterName, int conversationCount) =>
        ConfirmDestructiveAction(
            LanguageRuntime.GetString("Interaction.DeleteCharacter.Title"),
            LanguageRuntime.Format(
                "Interaction.DeleteCharacter.MessageFormat",
                characterName,
                conversationCount == 0
                    ? LanguageRuntime.GetString("Interaction.DeleteCharacter.NoChats")
                    : LanguageRuntime.Format("Interaction.DeleteCharacter.ChatCountFormat", conversationCount)),
            LanguageRuntime.GetString("Interaction.Action.DeleteCharacter"));

    public bool ConfirmConversationDeletion(string conversationTitle) =>
        ConfirmDestructiveAction(
            LanguageRuntime.GetString("Interaction.DeleteConversation.Title"),
            LanguageRuntime.Format("Interaction.DeleteConversation.MessageFormat", conversationTitle),
            LanguageRuntime.GetString("Interaction.Action.DeleteConversation"));

    public bool ConfirmShelfDeletion(string shelfName) =>
        ConfirmDestructiveAction(
            LanguageRuntime.GetString("Interaction.DeleteShelf.Title"),
            LanguageRuntime.Format("Interaction.DeleteShelf.MessageFormat", shelfName),
            LanguageRuntime.GetString("Interaction.Action.DeleteShelf"));

    public bool ConfirmPresetDeletion(string presetName) =>
        ConfirmDestructiveAction(
            LanguageRuntime.GetString("Interaction.DeletePreset.Title"),
            LanguageRuntime.Format("Interaction.DeletePreset.MessageFormat", presetName),
            LanguageRuntime.GetString("Interaction.Action.DeletePreset"));

    public bool ConfirmProviderDeletion(string providerName) =>
        ConfirmDestructiveAction(
            LanguageRuntime.GetString("Interaction.DeleteProvider.Title"),
            LanguageRuntime.Format("Interaction.DeleteProvider.MessageFormat", providerName),
            LanguageRuntime.GetString("Interaction.Action.DeleteProvider"));

    public bool ConfirmWorldbookDeletion(string worldbookName) =>
        ConfirmDestructiveAction(
            LanguageRuntime.GetString("Interaction.DeleteWorldbook.Title"),
            LanguageRuntime.Format("Interaction.DeleteWorldbook.MessageFormat", worldbookName),
            LanguageRuntime.GetString("Interaction.Action.DeleteWorldbook"));

    public bool ConfirmCampaignDeletion(string campaignTitle, int eventCount) =>
        ConfirmDestructiveAction(
            LanguageRuntime.GetString("Interaction.DeleteCampaign.Title"),
            LanguageRuntime.Format("Interaction.DeleteCampaign.MessageFormat", campaignTitle, eventCount),
            LanguageRuntime.GetString("Interaction.Action.DeleteCampaign"));

    public bool ConfirmSecretClear(string providerName) =>
        ConfirmDestructiveAction(
            LanguageRuntime.GetString("Interaction.ClearKey.Title"),
            LanguageRuntime.Format("Interaction.ClearKey.MessageFormat", providerName),
            LanguageRuntime.GetString("Interaction.Action.ClearKey"));

    public DataRootMigrationDecision ConfirmDataRootMigration(
        string currentRoot,
        string newRoot)
    {
        var actions = new[]
        {
            new DialogAction(
                LanguageRuntime.GetString("Interaction.Action.CopyCurrentData"),
                MessageBoxResult.Yes,
                DialogButtonRole.Primary,
                IsDefault: true),
            new DialogAction(
                LanguageRuntime.GetString("Interaction.Action.KeepTargetAsIs"),
                MessageBoxResult.No,
                DialogButtonRole.Secondary),
            new DialogAction(
                LanguageRuntime.GetString("Common.Cancel"),
                MessageBoxResult.Cancel,
                DialogButtonRole.Secondary,
                IsCancel: true)
        };
        var result = LocalizedMessageBox.Show(
            Application.Current.MainWindow,
            LanguageRuntime.Format("Interaction.ChangeDataRoot.MessageFormat", currentRoot, newRoot),
            LanguageRuntime.GetString("Interaction.ChangeDataRoot.Title"),
            actions,
            MessageBoxImage.Question,
            MessageBoxResult.Cancel);
        return result switch
        {
            MessageBoxResult.Yes => DataRootMigrationDecision.CopyCurrentData,
            MessageBoxResult.No => DataRootMigrationDecision.KeepTargetAsIs,
            _ => DataRootMigrationDecision.Cancel
        };
    }

    public bool ConfirmClearApiTestOutput(string outputDirectory) =>
        ConfirmDestructiveAction(
            LanguageRuntime.GetString("Interaction.Diagnostics.Clear.Title"),
            LanguageRuntime.Format(
                "Interaction.Diagnostics.Clear.MessageFormat",
                outputDirectory),
            LanguageRuntime.GetString("Interaction.Action.ClearLogs"));

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
