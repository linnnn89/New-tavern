using System.Windows;
using TavernDesk.App.Localization;

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
                LanguageRuntime.GetString("CustomProvider.NameRequired"),
                LanguageRuntime.GetString("CustomProvider.CannotAdd"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var baseUri)
            || baseUri.Scheme is not ("http" or "https"))
        {
            MessageBox.Show(
                this,
                LanguageRuntime.GetString("CustomProvider.BaseUrlAbsoluteRequired"),
                LanguageRuntime.GetString("CustomProvider.CannotAdd"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!string.IsNullOrEmpty(baseUri.Query)
            || !string.IsNullOrEmpty(baseUri.Fragment))
        {
            MessageBox.Show(
                this,
                LanguageRuntime.GetString("CustomProvider.BaseUrlNoQuery"),
                LanguageRuntime.GetString("CustomProvider.CannotAdd"),
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
                LanguageRuntime.GetString("CustomProvider.BaseUrlVersionRootRequired"),
                LanguageRuntime.GetString("CustomProvider.CannotAdd"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }
}
