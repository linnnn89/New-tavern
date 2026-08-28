using System.Collections.ObjectModel;
using TavernDesk.App.Localization;
using TavernDesk.App.Presentation;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;

namespace TavernDesk.App.ViewModels;

public sealed class DashboardViewModel : ViewModelBase
{
    private readonly ICharacterRepository _characters;
    private readonly IConversationRepository _conversations;
    private readonly IProviderProfileRepository _providers;
    private readonly Func<ConversationSummary, Task> _openConversation;
    private int _characterCount;
    private int _conversationCount;
    private int _providerCount;

    public DashboardViewModel(
        ICharacterRepository characters,
        IConversationRepository conversations,
        IProviderProfileRepository providers,
        Func<ConversationSummary, Task> openConversation)
    {
        _characters = characters;
        _conversations = conversations;
        _providers = providers;
        _openConversation = openConversation;
        OpenConversationCommand = new AsyncRelayCommand(OpenConversationAsync);
        ResumeLatestCommand = new AsyncRelayCommand(
            _ => OpenConversationAsync(LatestConversation),
            _ => HasRecentConversation);
    }

    public string Title => LanguageRuntime.GetString("Dashboard.Title");
    public string Subtitle => LanguageRuntime.GetString("Dashboard.Subtitle");
    public ObservableCollection<ConversationSummary> RecentConversations { get; } = [];
    public ConversationSummary? LatestConversation =>
        RecentConversations.Count > 0 ? RecentConversations[0] : null;
    public bool HasRecentConversation => LatestConversation is not null;
    public AsyncRelayCommand OpenConversationCommand { get; }
    public AsyncRelayCommand ResumeLatestCommand { get; }

    public int CharacterCount
    {
        get => _characterCount;
        private set => SetProperty(ref _characterCount, value);
    }

    public int ConversationCount
    {
        get => _conversationCount;
        private set => SetProperty(ref _conversationCount, value);
    }

    public int ProviderCount
    {
        get => _providerCount;
        private set => SetProperty(ref _providerCount, value);
    }

    public async Task LoadAsync()
    {
        CharacterCount = await _characters.CountAsync();
        ConversationCount = await _conversations.CountAsync();
        ProviderCount = await _providers.CountEnabledAsync();

        RecentConversations.Clear();
        foreach (var conversation in await _conversations.ListRecentAsync(6))
        {
            RecentConversations.Add(conversation);
        }

        OnPropertyChanged(nameof(LatestConversation));
        OnPropertyChanged(nameof(HasRecentConversation));
        ResumeLatestCommand.RaiseCanExecuteChanged();
    }

    private async Task OpenConversationAsync(object? parameter)
    {
        if (parameter is ConversationSummary conversation)
        {
            await _openConversation(conversation);
        }
    }
}
