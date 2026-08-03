using TavernDesk.App.Presentation;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;

namespace TavernDesk.App.ViewModels;

public sealed class ConversationListItemViewModel : ViewModelBase
{
    private bool _isSelected;
    private ConversationGenerationState _generationState;

    public ConversationListItemViewModel(
        ConversationSummary summary,
        ConversationGenerationState generationState,
        Func<string, Task>? openInNewWindow = null)
    {
        Summary = summary;
        _generationState = generationState;
        OpenInNewWindowCommand = new AsyncRelayCommand(
            () => openInNewWindow?.Invoke(Id) ?? Task.CompletedTask,
            () => openInNewWindow is not null);
    }

    public ConversationSummary Summary { get; }
    public string Id => Summary.Id;
    public string? CharacterId => Summary.CharacterId;
    public ConversationMode Mode => Summary.Mode;
    public string Title => Summary.Title;
    public string PreviewText => ConversationTextFormatter.Preview(Summary.LastMessagePreview);
    public string UpdatedText => ConversationTextFormatter.FriendlyTime(Summary.UpdatedAt);
    public DateTimeOffset UpdatedAt => Summary.UpdatedAt;
    public AsyncRelayCommand OpenInNewWindowCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string GenerationStatusText => _generationState.Status switch
    {
        ConversationGenerationStatus.Queued => "排队中",
        ConversationGenerationStatus.Streaming => "生成中",
        ConversationGenerationStatus.Stopping => "正在停止",
        ConversationGenerationStatus.Interrupted => "已中断",
        ConversationGenerationStatus.Failed => "生成失败",
        _ => string.Empty
    };

    public bool HasGenerationStatus => GenerationStatusText.Length > 0;

    public bool Matches(string query) =>
        Title.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Summary.LastMessagePreview.Contains(query, StringComparison.OrdinalIgnoreCase);

    public void ApplyGenerationState(ConversationGenerationState state)
    {
        if (state.ConversationId != Id)
        {
            return;
        }

        _generationState = state;
        OnPropertyChanged(nameof(GenerationStatusText));
        OnPropertyChanged(nameof(HasGenerationStatus));
    }
}
