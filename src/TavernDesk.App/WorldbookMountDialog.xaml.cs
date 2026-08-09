using System.Windows;
using TavernDesk.App.ViewModels;

namespace TavernDesk.App;

public partial class WorldbookMountDialog : Window
{
    public WorldbookMountDialog(WorldbookViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Dialog_OnLoaded(object sender, RoutedEventArgs e)
    {
        CharacterMountScrollViewer.ScrollToTop();
        CampaignMountScrollViewer.ScrollToTop();
    }
}
