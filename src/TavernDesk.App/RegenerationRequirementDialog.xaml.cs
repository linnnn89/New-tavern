using System.Windows;
using System.Windows.Input;

namespace TavernDesk.App;

public partial class RegenerationRequirementDialog : Window
{
    public RegenerationRequirementDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => RequirementEditor.Focus();
    }

    public string ResultText => RequirementEditor.Text.Trim();

    private void RequirementEditor_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            DialogResult = true;
        }
    }

    private void StartButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
