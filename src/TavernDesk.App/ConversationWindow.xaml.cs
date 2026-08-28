using System.Windows;
using TavernDesk.App.Presentation;
using TavernDesk.App.Services;
using TavernDesk.App.ViewModels;

namespace TavernDesk.App;

public partial class ConversationWindow : Window
{
    private readonly ChatViewModel _viewModel;
    private readonly WindowPlacementService _windowPlacement;

    public ConversationWindow(
        ChatViewModel viewModel,
        WindowPlacementService windowPlacement)
    {
        InitializeComponent();
        WindowChromeService.Attach(this);
        _viewModel = viewModel;
        _windowPlacement = windowPlacement;
        DataContext = viewModel;
        Closed += OnClosed;
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        try
        {
            await _windowPlacement.SaveAsync(
                this,
                "window.independentChat");
        }
        finally
        {
            // Closing this presentation must never cancel an in-flight stream.
            // The async operation retains its ViewModel until database cleanup;
            // the application-level session remains available to a reopened view.
            await _viewModel.DisposeAsync();
        }
    }
}
