using System.Windows.Controls;
using System.Windows.Input;

namespace TavernDesk.App.Views.Chat;

public partial class ChatPersonaPanel : UserControl
{
    public ChatPersonaPanel()
    {
        InitializeComponent();
    }

    private void PersonaEditorTextBox_OnPreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (sender is not TextBox textBox
            || !textBox.IsEnabled
            || textBox.IsReadOnly
            || textBox.GetCharacterIndexFromPoint(e.GetPosition(textBox), snapToText: false) >= 0)
        {
            return;
        }

        textBox.Focus();
        textBox.Select(textBox.Text?.Length ?? 0, 0);
        e.Handled = true;
    }
}
