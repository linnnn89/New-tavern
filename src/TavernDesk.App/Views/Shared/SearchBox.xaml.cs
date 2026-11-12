using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TavernDesk.App.Views.Shared;

public partial class SearchBox : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(SearchBox),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(
        nameof(Placeholder), typeof(string), typeof(SearchBox), new PropertyMetadata(string.Empty));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string Placeholder
    {
        get => (string)GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    public SearchBox() => InitializeComponent();

    private void Clear_OnClick(object sender, RoutedEventArgs e)
    {
        SearchInput.SetCurrentValue(TextBox.TextProperty, string.Empty);
        SearchInput.Focus();
    }

    private void SearchInput_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !string.IsNullOrEmpty(SearchInput.Text))
        {
            SearchInput.SetCurrentValue(TextBox.TextProperty, string.Empty);
            e.Handled = true;
        }
    }
}
