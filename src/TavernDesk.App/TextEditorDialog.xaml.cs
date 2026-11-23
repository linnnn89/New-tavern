using System.Windows;
using System.Windows.Input;
using TavernDesk.App.Localization;
using TavernDesk.App.Presentation;

namespace TavernDesk.App;

public partial class TextEditorDialog : Window
{
    public TextEditorDialog(string title, string prompt, string initialText)
    {
        InitializeComponent();
        WindowChromeService.Attach(this);
        Title = title;
        PromptText.Text = prompt;
        Editor.Text = initialText;
        Editor.Select(Editor.Text.Length, 0);
        Editor.Focus();
    }

    public string ResultText => Editor.Text;

    private void Editor_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            SaveButton_OnClick(sender, e);
        }
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Editor.Text))
        {
            LocalizedMessageBox.Show(
                this,
                LanguageRuntime.GetString("TextEditor.BodyRequired"),
                LanguageRuntime.GetString("Common.CannotSave"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }
}
