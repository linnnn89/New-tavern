using System.Windows;
using TavernDesk.App.Services;
using TavernDesk.App.ViewModels;

namespace TavernDesk.App;

public partial class MainWindow : Window
{
    private readonly WindowPlacementService _windowPlacement;

    public MainWindow(
        MainWindowViewModel viewModel,
        WindowPlacementService windowPlacement)
    {
        _windowPlacement = windowPlacement;
        InitializeComponent();
        DataContext = viewModel;
        Closed += OnClosed;
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        try
        {
            await _windowPlacement.SaveAsync(this, "window.main");
        }
        finally
        {
            // Closing the main window is a real application exit. Secondary
            // windows and page navigation must never route their Close here.
            Application.Current.Shutdown();
        }
    }
}
