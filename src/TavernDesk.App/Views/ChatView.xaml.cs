using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TavernDesk.App.Localization;
using System.Windows.Threading;
using TavernDesk.App.Presentation;
using TavernDesk.App.Services;
using TavernDesk.App.ViewModels;

namespace TavernDesk.App.Views;

public partial class ChatView : UserControl
{
    private const double RightPanelMinimumWidth = 260;
    private const double WindowChromeAllowance = 16;
    private readonly TimedPressFeedback _pressFeedback = new();
    private readonly DispatcherTimer _groupMemberMenuTimer;
    private readonly HashSet<ChatMessageItemViewModel> _observedMessages = [];
    private ChatViewModel? _observedViewModel;
    private bool _isOpeningMessageTools;
    private bool _scrollScheduled;
    private bool _isRightPanelCollapsed;
    private bool _isRightPanelAutoCollapsed;
    private double _rightPanelWidth = 406;
    private Window? _layoutHostWindow;
    private ContextMenu? _openGroupMemberMenu;

    public ChatView()
    {
        InitializeComponent();
        _groupMemberMenuTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _groupMemberMenuTimer.Tick += GroupMemberMenuTimer_OnTick;
        Loaded += ChatView_OnLoaded;
        Unloaded += ChatView_OnUnloaded;
        DataContextChanged += ChatView_OnDataContextChanged;
    }

    private void ChatArchiveMenuButton_OnClick(object sender, RoutedEventArgs e)
    {
        ChatArchiveMenuPopup.IsOpen = !ChatArchiveMenuPopup.IsOpen;
        e.Handled = true;
    }

    private void PersonaEditorTextBox_OnPreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (sender is not TextBox textBox
            || !textBox.IsEnabled
            || textBox.IsReadOnly
            || textBox.GetCharacterIndexFromPoint(e.GetPosition(textBox), snapToText: false) >= 0)
        {
            return;
        }

        textBox.Focus();
        textBox.Select(textBox.Text?.Length ?? 0, 0);
        e.Handled = true;
    }

    private void RightPanelToggleButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isRightPanelCollapsed)
        {
            if (RequiresResponsiveCollapse())
            {
                UpdateCollapsedToggleMetadata(isWidthConstrained: true);
                return;
            }

            ExpandRightPanel();
            return;
        }

        CollapseRightPanel(automatic: false);
    }

    private void CollapseRightPanel(bool automatic)
    {
        if (_isRightPanelCollapsed)
        {
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
        RightPanelSplitter.SetResourceReference(
            Control.BackgroundProperty,
            "BorderBrush");
        RightPanelCollapseArrow.Visibility = Visibility.Collapsed;
        RightPanelExpandArrow.Visibility = Visibility.Visible;
        _isRightPanelCollapsed = true;
        _isRightPanelAutoCollapsed = automatic;
        UpdateCollapsedToggleMetadata(isWidthConstrained: automatic);
    }

    private void ExpandRightPanel()
    {
        if (!_isRightPanelCollapsed)
        {
            return;
        }

        var fixedMinimumWidth = ExpandedLayoutMinimumWidth() - RightPanelMinimumWidth;
        var maximumSafeRightPanelWidth = Math.Max(
            RightPanelMinimumWidth,
            AvailableUnscaledChatWidth() - fixedMinimumWidth);
        var restoredRightPanelWidth = Math.Min(
            Math.Max(_rightPanelWidth, RightPanelMinimumWidth),
            maximumSafeRightPanelWidth);
        RightPanelColumn.MinWidth = RightPanelMinimumWidth;
        RightPanelColumn.Width = new GridLength(restoredRightPanelWidth);
        RightPanel.Visibility = Visibility.Visible;
        RightPanelSplitter.IsEnabled = true;
        RightPanelSplitter.Background = Brushes.Transparent;
        RightPanelCollapseArrow.Visibility = Visibility.Visible;
        RightPanelExpandArrow.Visibility = Visibility.Collapsed;
        var collapseLabel = LanguageRuntime.GetString("Chat.RightPanel.Collapse");
        RightPanelToggleButton.ToolTip = collapseLabel;
        AutomationProperties.SetName(RightPanelToggleButton, collapseLabel);
        _isRightPanelCollapsed = false;
        _isRightPanelAutoCollapsed = false;
    }

    private void UpdateCollapsedToggleMetadata(bool isWidthConstrained)
    {
        var label = isWidthConstrained
            ? LanguageRuntime.GetString("Chat.RightPanel.WidthConstrained")
            : LanguageRuntime.GetString("Chat.RightPanel.Expand");
        RightPanelToggleButton.ToolTip = label;
        AutomationProperties.SetName(RightPanelToggleButton, label);
    }

    private void AttachLayoutHostWindow()
    {
        var hostWindow = Window.GetWindow(this);
        if (ReferenceEquals(_layoutHostWindow, hostWindow))
        {
            return;
        }

        DetachLayoutHostWindow();
        _layoutHostWindow = hostWindow;
        if (_layoutHostWindow is not null)
        {
            _layoutHostWindow.SizeChanged += LayoutHostWindow_OnSizeChanged;
        }
    }

    private void DetachLayoutHostWindow()
    {
        if (_layoutHostWindow is not null)
        {
            _layoutHostWindow.SizeChanged -= LayoutHostWindow_OnSizeChanged;
            _layoutHostWindow = null;
        }
    }

    private void LayoutHostWindow_OnSizeChanged(
        object sender,
        SizeChangedEventArgs e) =>
        UpdateResponsiveLayout();

    private void UpdateResponsiveLayout()
    {
        var isWidthConstrained = RequiresResponsiveCollapse();
        if (isWidthConstrained)
        {
            if (!_isRightPanelCollapsed)
            {
                CollapseRightPanel(automatic: true);
            }
            else
            {
                UpdateCollapsedToggleMetadata(isWidthConstrained: true);
            }

            return;
        }

        if (_isRightPanelCollapsed && _isRightPanelAutoCollapsed)
        {
            ExpandRightPanel();
        }
        else if (_isRightPanelCollapsed)
        {
            UpdateCollapsedToggleMetadata(isWidthConstrained: false);
        }
    }

    private bool RequiresResponsiveCollapse()
    {
        if (_layoutHostWindow is null
            || _layoutHostWindow.ActualWidth <= 0)
        {
            return false;
        }

        return AvailableUnscaledChatWidth() < ExpandedLayoutMinimumWidth();
    }

    private double AvailableUnscaledChatWidth()
    {
        if (_layoutHostWindow is null)
        {
            return 0;
        }

        var scaleFactor = Math.Max(InterfaceSettingsRuntime.ScaleFactor, 0.01);
        var availableWidth = Math.Max(
            0,
            (_layoutHostWindow.ActualWidth - WindowChromeAllowance) / scaleFactor);
        return _layoutHostWindow is MainWindow mainWindow
            ? availableWidth - mainWindow.NavigationLayoutWidth
            : availableWidth;
    }

    private double ExpandedLayoutMinimumWidth() =>
        ChatLayoutRoot.Margin.Left
        + ChatLayoutRoot.Margin.Right
        + ConversationListColumn.MinWidth
        + ChatLayoutRoot.ColumnDefinitions[1].Width.Value
        + ConversationBodyColumn.MinWidth
        + ChatLayoutRoot.ColumnDefinitions[3].Width.Value
        + RightPanelMinimumWidth;

    private void ChatView_OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachLayoutHostWindow();
        InterfaceSettingsRuntime.Changed += InterfaceSettingsRuntime_OnChanged;
        ObserveViewModel(DataContext as ChatViewModel);
        UpdateResponsiveLayout();
        RequestAutoScroll();
    }

    private void ChatView_OnUnloaded(object sender, RoutedEventArgs e)
    {
        CloseGroupMemberMenu();
        InterfaceSettingsRuntime.Changed -= InterfaceSettingsRuntime_OnChanged;
        DetachLayoutHostWindow();
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

    private void InterfaceSettingsRuntime_OnChanged(object? sender, EventArgs e)
    {
        UpdateResponsiveLayout();
        RequestAutoScroll();
    }

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
                    var lastMessage = _observedViewModel?.Messages.LastOrDefault();
                    if (lastMessage is not null)
                    {
                        ConversationMessageList.ScrollIntoView(lastMessage);
                    }
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

    private void GroupMember_OnPreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (sender is not Button button
            || button.ContextMenu is not ContextMenu menu)
        {
            return;
        }

        CloseGroupMemberMenu();
        menu.PlacementTarget = button;
        _openGroupMemberMenu = menu;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void GroupMemberContextMenu_OnOpened(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu)
        {
            return;
        }

        _openGroupMemberMenu = menu;
        _groupMemberMenuTimer.Stop();
        _groupMemberMenuTimer.Start();
    }

    private void GroupMemberContextMenu_OnClosed(
        object sender,
        RoutedEventArgs e)
    {
        if (ReferenceEquals(_openGroupMemberMenu, sender))
        {
            _openGroupMemberMenu = null;
        }

        _groupMemberMenuTimer.Stop();
    }

    private void GroupMemberMenuClose_OnClick(object sender, RoutedEventArgs e)
    {
        CloseGroupMemberMenu();
        e.Handled = true;
    }

    private void GroupMemberMenuTimer_OnTick(object? sender, EventArgs e) =>
        CloseGroupMemberMenu();

    private void CloseGroupMemberMenu()
    {
        _groupMemberMenuTimer.Stop();
        if (_openGroupMemberMenu is not null)
        {
            var menu = _openGroupMemberMenu;
            _openGroupMemberMenu = null;
            menu.IsOpen = false;
        }
    }
}
