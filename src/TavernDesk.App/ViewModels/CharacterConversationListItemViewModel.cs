using TavernDesk.Core.Models;

namespace TavernDesk.App.ViewModels;

public sealed class CharacterConversationListItemViewModel
{
    public CharacterConversationListItemViewModel(ConversationSummary summary)
    {
        Summary = summary;
    }

    public ConversationSummary Summary { get; }
    public string Id => Summary.Id;
    public string Title => Summary.Title;
    public string PreviewText =>
        ConversationTextFormatter.NormalizePreview(Summary.LastMessagePreview);
    public string UpdatedText => ConversationTextFormatter.FriendlyTime(Summary.UpdatedAt);
}
