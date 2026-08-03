using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TavernDesk.App.Presentation;
using TavernDesk.App.ViewModels;

namespace TavernDesk.App.Views;

public partial class ChatView : UserControl
{
    private readonly TimedPressFeedback _pressFeedback = new();
    private bool _isOpeningMessageTools;

    public ChatView()
    {
        InitializeComponent();
    }

    private void MessageBubble_OnPreviewMouseRightButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
        if (sender is not Border { DataContext: ChatMessageItemViewModel message })
        {
            return;
        }

        if (message.OpenToolsCommand.CanExecute(null))
        {
            message.OpenToolsCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void MessagePlus_OnPreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        _pressFeedback.Press(sender, 2.5, TimeSpan.FromMilliseconds(70));
    }

    private async void MessagePlus_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button
            {
                DataContext: ChatMessageItemViewModel message
            } button
            || _isOpeningMessageTools)
        {
            return;
        }

        _isOpeningMessageTools = true;
        try
        {
            await _pressFeedback.ReleaseBeforeActionAsync(
                button,
                2.5,
                TimeSpan.FromMilliseconds(70),
                TimeSpan.FromMilliseconds(90),
                TimeSpan.FromMilliseconds(180));

            if (message.OpenToolsCommand.CanExecute(null))
            {
                message.OpenToolsCommand.Execute(null);
            }
        }
        finally
        {
            _isOpeningMessageTools = false;
        }
    }

    private void MessagePlus_OnMouseLeave(object sender, MouseEventArgs e)
    {
        _pressFeedback.Cancel(sender, TimeSpan.FromMilliseconds(150));
    }
}
