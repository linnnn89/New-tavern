using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TavernDesk.App.Presentation;
using TavernDesk.App.ViewModels;

namespace TavernDesk.App.Views;

public partial class DashboardView : UserControl
{
    private readonly TimedPressFeedback _pressFeedback = new();
    private bool _isOpeningConversation;

    public DashboardView()
    {
        InitializeComponent();
    }

    private void RecentConversation_OnPreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        _pressFeedback.Press(sender, 3, TimeSpan.FromMilliseconds(70));
    }

    private async void RecentConversation_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button
            || DataContext is not DashboardViewModel viewModel
            || _isOpeningConversation)
        {
            return;
        }

        _isOpeningConversation = true;
        try
        {
            await _pressFeedback.ReleaseBeforeActionAsync(
                button,
                3,
                TimeSpan.FromMilliseconds(70),
                TimeSpan.FromMilliseconds(90),
                TimeSpan.FromMilliseconds(190));

            if (viewModel.OpenConversationCommand.CanExecute(button.DataContext))
            {
                viewModel.OpenConversationCommand.Execute(button.DataContext);
            }
        }
        finally
        {
            _isOpeningConversation = false;
        }
    }

    private void RecentConversation_OnMouseLeave(object sender, MouseEventArgs e)
    {
        _pressFeedback.Cancel(sender, TimeSpan.FromMilliseconds(150));
    }
}
