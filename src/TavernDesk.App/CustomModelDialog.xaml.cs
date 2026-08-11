using System.Windows;
using TavernDesk.App.Localization;

namespace TavernDesk.App;

public partial class CustomModelDialog : Window
{
    public CustomModelDialog(string initialText)
    {
        InitializeComponent();
        Title = LanguageRuntime.GetString("CustomModel.Title");
        PromptText.Text = LanguageRuntime.GetString("CustomModel.Instruction");
        ModelNameEditor.Text = initialText;
        Loaded += (_, _) =>
        {
            ModelNameEditor.Focus();
            ModelNameEditor.SelectAll();
        };
    }

    public string ResultText => ModelNameEditor.Text.Trim();

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ModelNameEditor.Text))
        {
            MessageBox.Show(
                this,
                LanguageRuntime.GetString("CustomModel.Required"),
                LanguageRuntime.GetString("Common.CannotSave"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }
}
