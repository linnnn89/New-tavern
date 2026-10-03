using TavernDesk.App.Localization;
using TavernDesk.App.Presentation;
using TavernDesk.App.Services;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;

namespace TavernDesk.App.ViewModels;

public sealed class ChatCharacterPromptViewModel : ViewModelBase
{
    private readonly ICharacterRepository _characters;
    private readonly IUserInteractionService _interaction;
    private readonly Func<bool> _isSingleCharacterConversation;
    private readonly Func<string?> _selectedConversationId;
    private readonly Action<Character> _onCharacterSaved;
    private readonly Func<Task> _refreshContextNow;
    private string _characterPromptCharacterId = string.Empty;
    private string _characterPromptCharacterName = LanguageRuntime.GetString("Chat.Character.None");
    private string _characterSystemPrompt = string.Empty;
    private string _characterPostHistoryInstructions = string.Empty;
    private string _characterPromptStatus =
        LanguageRuntime.GetString("Chat.CharacterPrompt.Select");

    public ChatCharacterPromptViewModel(
        ICharacterRepository characters,
        IUserInteractionService interaction,
        Func<bool> isSingleCharacterConversation,
        Func<string?> selectedConversationId,
        Action<Character> onCharacterSaved,
        Func<Task> refreshContextNow)
    {
        _characters = characters;
        _interaction = interaction;
        _isSingleCharacterConversation = isSingleCharacterConversation;
        _selectedConversationId = selectedConversationId;
        _onCharacterSaved = onCharacterSaved;
        _refreshContextNow = refreshContextNow;
        EditCharacterSystemPromptCommand = new AsyncRelayCommand(
            EditCharacterSystemPromptAsync,
            CanEditCharacterPrompt);
        EditCharacterPostHistoryCommand = new AsyncRelayCommand(
            EditCharacterPostHistoryAsync,
            CanEditCharacterPrompt);
    }

    public AsyncRelayCommand EditCharacterSystemPromptCommand { get; }
    public AsyncRelayCommand EditCharacterPostHistoryCommand { get; }

    public string CharacterPromptCharacterName
    {
        get => _characterPromptCharacterName;
        private set => SetProperty(ref _characterPromptCharacterName, value);
    }

    public string CharacterSystemPrompt
    {
        get => _characterSystemPrompt;
        private set => SetProperty(ref _characterSystemPrompt, value);
    }

    public string CharacterPostHistoryInstructions
    {
        get => _characterPostHistoryInstructions;
        private set => SetProperty(ref _characterPostHistoryInstructions, value);
    }

    public string CharacterPromptStatus
    {
        get => _characterPromptStatus;
        private set => SetProperty(ref _characterPromptStatus, value);
    }

    private bool CanEditCharacterPrompt() =>
        _isSingleCharacterConversation()
        && !string.IsNullOrWhiteSpace(_characterPromptCharacterId);

    private Task EditCharacterSystemPromptAsync() =>
        EditCharacterPromptAsync(editPostHistory: false);

    private Task EditCharacterPostHistoryAsync() =>
        EditCharacterPromptAsync(editPostHistory: true);

    private async Task EditCharacterPromptAsync(bool editPostHistory)
    {
        var characterId = _characterPromptCharacterId;
        var conversationId = _selectedConversationId();
        if (!CanEditCharacterPrompt()
            || string.IsNullOrWhiteSpace(characterId)
            || string.IsNullOrWhiteSpace(conversationId))
        {
            return;
        }

        try
        {
            var character = await _characters.GetAsync(characterId);
            if (character is null)
            {
                CharacterPromptStatus = LanguageRuntime.GetString("Chat.CharacterPrompt.Missing");
                return;
            }

            var buffer = new CharacterEditBuffer();
            buffer.Load(character);
            var currentText = editPostHistory
                ? buffer.PostHistoryInstructions
                : buffer.SystemPrompt;
            var edited = await _interaction.EditTextAsync(
                editPostHistory
                    ? LanguageRuntime.Format(
                        "Chat.CharacterPrompt.EditPostHistoryFormat",
                        character.Name)
                    : LanguageRuntime.Format(
                        "Chat.CharacterPrompt.EditSystemFormat",
                        character.Name),
                editPostHistory
                    ? LanguageRuntime.GetString("Chat.CharacterPrompt.PostHistoryHelp")
                    : LanguageRuntime.GetString("Chat.CharacterPrompt.SystemHelp"),
                currentText);
            if (edited is null
                || string.Equals(edited, currentText, StringComparison.Ordinal))
            {
                return;
            }

            if (editPostHistory)
            {
                buffer.PostHistoryInstructions = edited;
            }
            else
            {
                buffer.SystemPrompt = edited;
            }

            buffer.ApplyTo(character);
            character.UpdatedAt = DateTimeOffset.Now;
            await _characters.UpsertAsync(character);
            _onCharacterSaved(character);

            if (_selectedConversationId() == conversationId)
            {
                Apply(character);
                CharacterPromptStatus = editPostHistory
                    ? LanguageRuntime.GetString("Chat.CharacterPrompt.PostHistorySaved")
                    : LanguageRuntime.GetString("Chat.CharacterPrompt.SystemSaved");
                await _refreshContextNow();
            }
        }
        catch (Exception exception)
        {
            CharacterPromptStatus = LanguageRuntime.Format(
                "Chat.CharacterPrompt.SaveFailedFormat",
                LanguageRuntime.ErrorMessage(exception));
        }
    }

    public void Apply(Character? character)
    {
        if (character is null)
        {
            _characterPromptCharacterId = string.Empty;
            CharacterPromptCharacterName = LanguageRuntime.GetString("Chat.Character.None");
            CharacterSystemPrompt = string.Empty;
            CharacterPostHistoryInstructions = string.Empty;
            CharacterPromptStatus =
                LanguageRuntime.GetString("Chat.CharacterPrompt.Select");
        }
        else
        {
            var buffer = new CharacterEditBuffer();
            buffer.Load(character);
            _characterPromptCharacterId = character.Id;
            CharacterPromptCharacterName = character.Name;
            CharacterSystemPrompt = buffer.SystemPrompt;
            CharacterPostHistoryInstructions =
                buffer.PostHistoryInstructions;
            CharacterPromptStatus =
                string.IsNullOrWhiteSpace(buffer.SystemPrompt)
                && string.IsNullOrWhiteSpace(buffer.PostHistoryInstructions)
                    ? LanguageRuntime.GetString("Chat.CharacterPrompt.Empty")
                    : LanguageRuntime.GetString("Chat.CharacterPrompt.FromCard");
        }

        RaiseCanExecuteChanged();
    }

    public void RaiseCanExecuteChanged()
    {
        EditCharacterSystemPromptCommand.RaiseCanExecuteChanged();
        EditCharacterPostHistoryCommand.RaiseCanExecuteChanged();
    }
}
