using System.ComponentModel;
using System.Windows;
using TavernDesk.App.Services;
using TavernDesk.App.ViewModels;

namespace TavernDesk.App;

public partial class MainWindow : Window
{
    private readonly WindowPlacementService _windowPlacement;
    private bool _closeConfirmed;
    private bool _closeConfirmationInProgress;

    public MainWindow(
        MainWindowViewModel viewModel,
        WindowPlacementService windowPlacement)
    {
        _windowPlacement = windowPlacement;
        InitializeComponent();
        DataContext = viewModel;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeConfirmed || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        e.Cancel = true;
        if (_closeConfirmationInProgress)
        {
            return;
        }

        _closeConfirmationInProgress = true;
        Dispatcher.BeginInvoke(async () => await ConfirmAndCloseAsync(viewModel));
    }

    private async Task ConfirmAndCloseAsync(MainWindowViewModel viewModel)
    {
        try
        {
            if (!await viewModel.ConfirmCanCloseAsync())
            {
                return;
            }

            _closeConfirmed = true;
            Close();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "无法保存大厅草稿",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _closeConfirmationInProgress = false;
        }
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
