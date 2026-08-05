using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TavernDesk.App.Presentation;
using TavernDesk.App.Services;
using TavernDesk.App.ViewModels;

namespace TavernDesk.App.Views;

public partial class ChatView : UserControl
{
    private readonly TimedPressFeedback _pressFeedback = new();
    private readonly HashSet<ChatMessageItemViewModel> _observedMessages = [];
    private ChatViewModel? _observedViewModel;
    private bool _isOpeningMessageTools;
    private bool _scrollScheduled;
    private bool _isRightPanelCollapsed;
    private double _rightPanelWidth = 300;

    public ChatView()
    {
        InitializeComponent();
        Loaded += ChatView_OnLoaded;
        Unloaded += ChatView_OnUnloaded;
        DataContextChanged += ChatView_OnDataContextChanged;
    }

    private void RightPanelToggleButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isRightPanelCollapsed)
        {
            RightPanelColumn.MinWidth = 240;
            RightPanelColumn.Width = new GridLength(Math.Max(_rightPanelWidth, 240));
            RightPanel.Visibility = Visibility.Visible;
            RightPanelSplitter.IsEnabled = true;
            RightPanelSplitter.Background = Brushes.Transparent;
            RightPanelCollapseArrow.Visibility = Visibility.Visible;
            RightPanelExpandArrow.Visibility = Visibility.Collapsed;
            RightPanelToggleButton.ToolTip = "折叠右侧栏";
            AutomationProperties.SetName(RightPanelToggleButton, "折叠右侧栏");
            _isRightPanelCollapsed = false;
            return;
        }

        if (RightPanelColumn.ActualWidth >= RightPanelColumn.MinWidth)
        {
            _rightPanelWidth = RightPanelColumn.ActualWidth;
        }

        RightPanelColumn.MinWidth = 0;
        RightPanelColumn.Width = new GridLength(0);
        RightPanel.Visibility = Visibility.Collapsed;
        RightPanelSplitter.IsEnabled = false;
        RightPanelSplitter.Background = (Brush)FindResource("BorderBrush");
        RightPanelCollapseArrow.Visibility = Visibility.Collapsed;
        RightPanelExpandArrow.Visibility = Visibility.Visible;
        RightPanelToggleButton.ToolTip = "展开右侧栏";
        AutomationProperties.SetName(RightPanelToggleButton, "展开右侧栏");
        _isRightPanelCollapsed = true;
    }

    private void ChatView_OnLoaded(object sender, RoutedEventArgs e)
    {
        InterfaceSettingsRuntime.Changed += InterfaceSettingsRuntime_OnChanged;
        ObserveViewModel(DataContext as ChatViewModel);
        RequestAutoScroll();
    }

    private void ChatView_OnUnloaded(object sender, RoutedEventArgs e)
    {
        InterfaceSettingsRuntime.Changed -= InterfaceSettingsRuntime_OnChanged;
        ObserveViewModel(null);
    }

    private void ChatView_OnDataContextChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        ObserveViewModel(e.NewValue as ChatViewModel);
        RequestAutoScroll();
    }

    private void ObserveViewModel(ChatViewModel? viewModel)
    {
        if (ReferenceEquals(_observedViewModel, viewModel))
        {
            return;
        }

        if (_observedViewModel is not null)
        {
            _observedViewModel.Messages.CollectionChanged -= Messages_OnCollectionChanged;
        }

        ClearObservedMessages();
        _observedViewModel = viewModel;
        if (_observedViewModel is null)
        {
            return;
        }

        _observedViewModel.Messages.CollectionChanged += Messages_OnCollectionChanged;
        foreach (var message in _observedViewModel.Messages)
        {
            ObserveMessage(message);
        }
    }

    private void Messages_OnCollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            ClearObservedMessages();
            if (_observedViewModel is not null)
            {
                foreach (var message in _observedViewModel.Messages)
                {
                    ObserveMessage(message);
                }
            }
        }
        else
        {
            if (e.OldItems is not null)
            {
                foreach (ChatMessageItemViewModel message in e.OldItems)
                {
                    StopObservingMessage(message);
                }
            }

            if (e.NewItems is not null)
            {
                foreach (ChatMessageItemViewModel message in e.NewItems)
                {
                    ObserveMessage(message);
                }
            }
        }

        RequestAutoScroll();
    }

    private void ObserveMessage(ChatMessageItemViewModel message)
    {
        if (_observedMessages.Add(message))
        {
            message.PropertyChanged += Message_OnPropertyChanged;
        }
    }

    private void StopObservingMessage(ChatMessageItemViewModel message)
    {
        if (_observedMessages.Remove(message))
        {
            message.PropertyChanged -= Message_OnPropertyChanged;
        }
    }

    private void ClearObservedMessages()
    {
        foreach (var message in _observedMessages)
        {
            message.PropertyChanged -= Message_OnPropertyChanged;
        }

        _observedMessages.Clear();
    }

    private void Message_OnPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatMessageItemViewModel.DisplayContent)
            && sender is ChatMessageItemViewModel message
            && ReferenceEquals(_observedViewModel?.Messages.LastOrDefault(), message))
        {
            RequestAutoScroll();
        }
    }

    private void InterfaceSettingsRuntime_OnChanged(object? sender, EventArgs e) =>
        RequestAutoScroll();

    private void RequestAutoScroll()
    {
        if (!IsLoaded
            || !InterfaceSettingsRuntime.ChatAutoScrollEnabled
            || _scrollScheduled)
        {
            return;
        }

        _scrollScheduled = true;
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                _scrollScheduled = false;
                if (IsLoaded && InterfaceSettingsRuntime.ChatAutoScrollEnabled)
                {
                    ConversationScrollViewer.ScrollToEnd();
                }
            }));
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
