using System.Collections.ObjectModel;
using TavernDesk.App.Localization;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;

namespace TavernDesk.App.ViewModels;

/// <summary>Owns conversation grouping, filtering, expansion and the character cache for one window.</summary>
public sealed class ConversationBrowserViewModel
{
    private readonly IConversationRepository _repository;
    private readonly ICharacterRepository _characters;
    private readonly IConversationGenerationCoordinator _generationCoordinator;
    private readonly Func<string, Task>? _openConversationWindow;
    private readonly Func<ConversationListItemViewModel, Task> _deleteConversation;
    private readonly SemaphoreSlim _reloadGate = new(1, 1);
    private readonly List<CharacterConversationGroupViewModel> _allGroups = [];
    private readonly Dictionary<string, Character> _characterLookup = new(StringComparer.Ordinal);

    public ConversationBrowserViewModel(IConversationRepository repository,
        ICharacterRepository characters, IConversationGenerationCoordinator generationCoordinator,
        Func<string, Task>? openConversationWindow, Func<ConversationListItemViewModel, Task> deleteConversation)
    {
        _repository = repository;
        _characters = characters;
        _generationCoordinator = generationCoordinator;
        _openConversationWindow = openConversationWindow;
        _deleteConversation = deleteConversation;
    }

    public ObservableCollection<CharacterConversationGroupViewModel> Groups { get; } = [];
    public IReadOnlyList<CharacterConversationGroupViewModel> AllGroups => _allGroups;
    public IReadOnlyDictionary<string, Character> Characters => _characterLookup;
    public void UpdateCharacter(Character character) => _characterLookup[character.Id] = character;

    public async Task ReloadAsync(Func<string?> preferredSelection, Action<string?> applySelection)
    {
        await _reloadGate.WaitAsync();
        try
        {
            // Resolve selection after acquiring the gate: overlapping completion
            // refreshes must preserve the selection at execution time.
            var preferred = preferredSelection();
            await ReloadCoreAsync(preferred);
            applySelection(preferred);
        }
        finally { _reloadGate.Release(); }
    }

    public void ApplyFilter(string query)
    {
        Groups.Clear();
        foreach (var group in _allGroups.Where(group => group.ApplyFilter(query))) Groups.Add(group);
    }

    public ConversationListItemViewModel? FindConversation(string? conversationId) =>
        conversationId is null ? null : _allGroups.Select(group => group.FindConversation(conversationId))
            .FirstOrDefault(item => item is not null);

    private async Task ReloadCoreAsync(string? preferredConversationId)
    {
        var expandedOwners = _allGroups
            .Where(group => group.IsExpanded)
            .Select(group => group.OwnerId)
            .ToHashSet(StringComparer.Ordinal);
        var characterTask = _characters.ListAsync();
        var conversationTask = _repository.ListAllAsync();
        await Task.WhenAll(characterTask, conversationTask);

        _characterLookup.Clear();
        foreach (var character in characterTask.Result)
        {
            _characterLookup[character.Id] = character;
        }
        _allGroups.Clear();

        foreach (var grouping in conversationTask.Result
                     .GroupBy(
                         conversation => conversation.Mode == ConversationMode.Group
                             ? "__group__"
                             : conversation.CharacterId ?? "__deleted__",
                         StringComparer.Ordinal))
        {
            var items = grouping
                .Select(summary => new ConversationListItemViewModel(
                    summary,
                    _generationCoordinator.GetState(summary.Id),
                    _openConversationWindow,
                    _deleteConversation))
                .ToArray();
            if (items.Length == 0)
            {
                continue;
            }

            CharacterConversationGroupViewModel group;
            if (grouping.Key == "__group__")
            {
                group = new CharacterConversationGroupViewModel(
                    "__group__",
                    LanguageRuntime.GetString("Chat.Group.Label"),
                    string.Empty,
                    isGroup: true,
                    items);
            }
            else if (grouping.Key == "__deleted__")
            {
                group = new CharacterConversationGroupViewModel(
                    "__deleted__",
                    LanguageRuntime.GetString("Chat.DeletedCharacter.Label"),
                    string.Empty,
                    isGroup: false,
                    items);
            }
            else if (_characterLookup.TryGetValue(grouping.Key, out var character))
            {
                group = new CharacterConversationGroupViewModel(
                    character.Id,
                    character.Name,
                    character.AvatarPath,
                    isGroup: false,
                    items);
            }
            else
            {
                group = new CharacterConversationGroupViewModel(
                    grouping.Key,
                    LanguageRuntime.GetString("Chat.DeletedCharacter.Label"),
                    string.Empty,
                    isGroup: false,
                    items);
            }

            group.IsExpanded = expandedOwners.Contains(group.OwnerId)
                               || group.FindConversation(preferredConversationId ?? string.Empty) is not null;
            _allGroups.Add(group);
        }

        _allGroups.Sort((left, right) =>
        {
            var updatedComparison = right.UpdatedAt.CompareTo(left.UpdatedAt);
            return updatedComparison != 0
                ? updatedComparison
                : string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
        });
    }

}
