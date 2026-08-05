using System.Windows;

namespace TavernDesk.App;

public partial class RegenerationRequirementDialog : Window
{
    public RegenerationRequirementDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => RequirementEditor.Focus();
    }

    public string ResultText => RequirementEditor.Text.Trim();

    private void StartButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
