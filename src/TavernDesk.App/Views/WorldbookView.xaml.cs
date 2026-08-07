using System.Windows;
using System.Windows.Controls;

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

    private void MountManagementButton_OnClick(object sender, RoutedEventArgs e)
    {
        MountManagementPopup.IsOpen = !MountManagementPopup.IsOpen;
        e.Handled = true;
    }
}
