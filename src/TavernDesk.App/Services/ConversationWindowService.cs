using TavernDesk.App.ViewModels;

namespace TavernDesk.App.Services;

public sealed class ConversationWindowService
{
    private readonly ChatViewModelFactory _viewModels;
    private readonly WindowPlacementService _windowPlacement;

    public ConversationWindowService(
        ChatViewModelFactory viewModels,
        WindowPlacementService windowPlacement)
    {
        _viewModels = viewModels;
        _windowPlacement = windowPlacement;
    }

    public async Task OpenAsync(string conversationId)
    {
        var viewModel = _viewModels.Create();
        try
        {
            await viewModel.LoadAsync();
            await viewModel.OpenConversationAsync(conversationId);
            var window = new ConversationWindow(viewModel, _windowPlacement);
            await _windowPlacement.RestoreAsync(
                window,
                "window.independentChat",
                1260,
                820);
            window.Show();
        }
        catch
        {
            await viewModel.DisposeAsync();
            throw;
        }
    }
}
