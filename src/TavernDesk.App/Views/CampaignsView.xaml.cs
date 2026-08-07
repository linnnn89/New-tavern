using System.Windows;
using System.Windows.Controls;
using TavernDesk.App.ViewModels;

namespace TavernDesk.App.Views;

public partial class CampaignsView : UserControl
{
    public CampaignsView()
    {
        InitializeComponent();
    }

    private void MemorySettingsButton_OnClick(
        object sender,
        RoutedEventArgs e)
    {
        if (DataContext is not CampaignsViewModel viewModel)
        {
            return;
        }

        viewModel.PrepareCampaignMemorySettings();
        var dialog = new CampaignMemorySettingsDialog
        {
            Owner = Window.GetWindow(this),
            DataContext = viewModel
        };
        dialog.ShowDialog();
    }

    private void GameMenuButton_OnClick(object sender, RoutedEventArgs e)
    {
        GameMenuPopup.IsOpen = !GameMenuPopup.IsOpen;
        e.Handled = true;
    }
}
