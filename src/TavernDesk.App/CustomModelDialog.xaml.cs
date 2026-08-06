using System.Windows;

namespace TavernDesk.App;

public partial class CustomModelDialog : Window
{
    public CustomModelDialog(string initialText)
    {
        InitializeComponent();
        Title = "添加自定义模型";
        PromptText.Text =
            "输入要保存到本地目录的任意模型 ID 或名称";
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
                "模型 ID 或名称不能为空。",
                "无法保存",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }
}
