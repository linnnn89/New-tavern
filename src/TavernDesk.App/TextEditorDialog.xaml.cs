using System.Windows;

namespace TavernDesk.App;

public partial class TextEditorDialog : Window
{
    public TextEditorDialog(string title, string prompt, string initialText)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        Editor.Text = initialText;
        Editor.Select(Editor.Text.Length, 0);
        Editor.Focus();
    }

    public string ResultText => Editor.Text;

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Editor.Text))
        {
            MessageBox.Show(
                this,
                "正文不能为空。",
                "无法保存",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }
}
