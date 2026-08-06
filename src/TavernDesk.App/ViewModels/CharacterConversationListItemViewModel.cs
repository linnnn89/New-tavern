using TavernDesk.App.Presentation;
using TavernDesk.Core.Models;

namespace TavernDesk.App.ViewModels;

public sealed class CharacterConversationListItemViewModel
{
    public CharacterConversationListItemViewModel(
        ConversationSummary summary,
        Func<string, string, Task>? deleteConversation = null)
    {
        Summary = summary;
        DeleteConversationCommand = new AsyncRelayCommand(
            () => deleteConversation?.Invoke(Id, Title) ?? Task.CompletedTask,
            () => deleteConversation is not null);
    }

    public ConversationSummary Summary { get; }
    public string Id => Summary.Id;
    public string Title => Summary.Title;
    public string PreviewText =>
        ConversationTextFormatter.NormalizePreview(Summary.LastMessagePreview);
    public string UpdatedText => ConversationTextFormatter.FriendlyTime(Summary.UpdatedAt);
    public AsyncRelayCommand DeleteConversationCommand { get; }
}
