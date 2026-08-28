using System.Windows;
using System.Windows.Controls;
using TavernDesk.App.Localization;
using TavernDesk.App.Presentation;

namespace TavernDesk.App.Views;

public partial class ShellCaptionButtons : UserControl
{
    public ShellCaptionButtons()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private Window? HostWindow => Window.GetWindow(this);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var window = HostWindow;
        if (window is null)
        {
            return;
        }

        window.StateChanged += WindowOnStateChanged;
        RefreshMaximizeGlyph(window);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        var window = HostWindow;
        if (window is not null)
        {
            window.StateChanged -= WindowOnStateChanged;
        }
    }

    private void WindowOnStateChanged(object? sender, EventArgs e)
    {
        if (sender is Window window)
        {
            RefreshMaximizeGlyph(window);
        }
    }

    private void RefreshMaximizeGlyph(Window window)
    {
        var maximized = window.WindowState == WindowState.Maximized;
        MaximizeGlyph.Text = maximized ? "\uE923" : "\uE922";
        var label = LanguageRuntime.GetString(
            maximized ? "Shell.Caption.Restore" : "Shell.Caption.Maximize");
        MaximizeButton.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(MaximizeButton, label);
    }

    private void MinimizeButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (HostWindow is { } window)
        {
            WindowChromeService.Minimize(window);
        }
    }

    private void MaximizeButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (HostWindow is { } window)
        {
            WindowChromeService.ToggleMaximize(window);
        }
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (HostWindow is { } window)
        {
            WindowChromeService.Close(window);
        }
    }
}
