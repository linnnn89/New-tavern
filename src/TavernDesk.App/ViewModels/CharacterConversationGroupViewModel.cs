using System.Collections.ObjectModel;
using TavernDesk.App.Presentation;

using TavernDesk.App.Localization;

namespace TavernDesk.App.ViewModels;

public sealed class CharacterConversationGroupViewModel : ViewModelBase
{
    private bool _isExpanded;
    private bool _searchIsActive;
    private bool _expandedBeforeSearch;

    public CharacterConversationGroupViewModel(
        string ownerId,
        string name,
        string avatarPath,
        bool isGroup,
        IEnumerable<ConversationListItemViewModel> conversations)
    {
        OwnerId = ownerId;
        Name = name;
        AvatarPath = avatarPath;
        IsGroup = isGroup;
        AllConversations = conversations
            .OrderByDescending(item => item.UpdatedAt)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        foreach (var conversation in AllConversations)
        {
            VisibleConversations.Add(conversation);
        }

        ToggleCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    public string OwnerId { get; }
    public string Name { get; }
    public string AvatarPath { get; }
    public bool IsGroup { get; }
    public IReadOnlyList<ConversationListItemViewModel> AllConversations { get; }
    public ObservableCollection<ConversationListItemViewModel> VisibleConversations { get; } = [];
    public RelayCommand ToggleCommand { get; }
    public DateTimeOffset UpdatedAt => AllConversations[0].UpdatedAt;
    public string UpdatedText => ConversationTextFormatter.FriendlyTime(UpdatedAt);
    public string LatestPreview => AllConversations[0].PreviewText;
    public string ConversationCountText => LanguageRuntime.Format(
        "Conversation.CountFormat",
        AllConversations.Count);

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public bool ApplyFilter(string query)
    {
        var trimmed = query.Trim();
        var isSearching = trimmed.Length > 0;
        if (isSearching && !_searchIsActive)
        {
            _expandedBeforeSearch = IsExpanded;
        }

        VisibleConversations.Clear();
        var ownerMatches = isSearching
            && Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase);
        foreach (var conversation in AllConversations.Where(item =>
                     !isSearching || ownerMatches || item.Matches(trimmed)))
        {
            VisibleConversations.Add(conversation);
        }

        if (isSearching)
        {
            IsExpanded = VisibleConversations.Count > 0;
        }
        else if (_searchIsActive)
        {
            IsExpanded = _expandedBeforeSearch;
        }

        _searchIsActive = isSearching;
        return !isSearching
            ? AllConversations.Count > 0
            : ownerMatches || VisibleConversations.Count > 0;
    }

    public ConversationListItemViewModel? FindConversation(string conversationId) =>
        AllConversations.FirstOrDefault(item => item.Id == conversationId);
}
