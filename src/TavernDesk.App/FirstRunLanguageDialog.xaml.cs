using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using TavernDesk.App.Localization;

namespace TavernDesk.App;

public partial class FirstRunLanguageDialog : Window
{
    public FirstRunLanguageDialog()
    {
        InitializeComponent();
    }

    public string? SelectedCultureName { get; private set; }

    private void LanguageButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string cultureName })
        {
            return;
        }

        SelectedCultureName = LanguageRuntime.NormalizeCultureName(cultureName);
        DialogResult = true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (DialogResult != true)
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
    }
}
