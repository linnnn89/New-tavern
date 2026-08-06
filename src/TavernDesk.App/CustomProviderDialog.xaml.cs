using System.Windows;

namespace TavernDesk.App;

public partial class CustomProviderDialog : Window
{
    public CustomProviderDialog()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            NameEditor.Focus();
            NameEditor.SelectAll();
        };
    }

    public string ProviderName => NameEditor.Text.Trim();
    public string BaseUrl => BaseUrlEditor.Text.Trim();

    private void AddButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (ProviderName.Length == 0)
        {
            MessageBox.Show(
                this,
                "接入商名称不能为空。",
                "无法添加",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var baseUri)
            || baseUri.Scheme is not ("http" or "https"))
        {
            MessageBox.Show(
                this,
                "API 地址必须是完整的 http 或 https 地址。",
                "无法添加",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!string.IsNullOrEmpty(baseUri.Query)
            || !string.IsNullOrEmpty(baseUri.Fragment))
        {
            MessageBox.Show(
                this,
                "API 地址不能包含查询参数或片段。",
                "无法添加",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (baseUri.AbsolutePath.EndsWith(
                "/chat",
                StringComparison.OrdinalIgnoreCase)
            || baseUri.AbsolutePath.EndsWith(
                "/chat/completions",
                StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(
                this,
                "请填写到 /api/v1 或 /v1 结束，不要加入 /chat。",
                "无法添加",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }
}
