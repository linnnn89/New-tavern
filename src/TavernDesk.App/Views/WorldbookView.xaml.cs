using System.Windows;
using System.Windows.Controls;
using TavernDesk.App;
using TavernDesk.App.Localization;
using TavernDesk.App.ViewModels;

namespace TavernDesk.App.Views;

public partial class WorldbookView : UserControl
{
    public WorldbookView()
    {
        InitializeComponent();
    }

    private void ImportOptionsButton_OnClick(object sender, RoutedEventArgs e)
    {
        ImportOptionsPopup.IsOpen = !ImportOptionsPopup.IsOpen;
        e.Handled = true;
    }

    private async void MountManagementButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not WorldbookViewModel viewModel
            || viewModel.SelectedBook is null)
        {
            LocalizedMessageBox.Show(
                Window.GetWindow(this),
                LanguageRuntime.GetString("Worldbook.Mount.SelectFirst"),
                LanguageRuntime.GetString("Worldbook.Mount.Title"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        MountManagementButton.IsEnabled = false;
        try
        {
            await viewModel.RefreshSelectedBookAsync();
            var dialog = new WorldbookMountDialog(viewModel)
            {
                Owner = Window.GetWindow(this)
            };
            dialog.ShowDialog();

            // Re-read persisted mounts so closing the window discards any
            // unchecked/checked values that were not explicitly saved.
            await viewModel.RefreshSelectedBookAsync();
        }
        finally
        {
            MountManagementButton.IsEnabled = true;
        }

        e.Handled = true;
    }
}
