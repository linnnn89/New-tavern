using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TavernDesk.App.Localization;

namespace TavernDesk.App;

// Deliberately independent of the previewed application scale and font settings.
public sealed class SafeChoiceDialog : Window
{
    public bool? Choice { get; private set; }
    public SafeChoiceDialog(string title, string message, string accept, string reject, bool timed = false)
    {
        Title = title;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = Math.Min(440, SystemParameters.WorkArea.Width);
        SizeToContent = SizeToContent.Height;
        MaxHeight = SystemParameters.WorkArea.Height;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 14;
        Background = SystemColors.WindowBrush;
        Foreground = SystemColors.WindowTextBrush;
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap,
            Foreground = SystemColors.WindowTextBrush, FontSize = 14 });
        var countdown = new TextBlock { Margin = new Thickness(0, 12, 0, 8),
            Foreground = SystemColors.WindowTextBrush, FontSize = 14 };
        panel.Children.Add(countdown);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var yes = MakeButton(accept, "SafeChoiceAccept");
        var no = MakeButton(reject, "SafeChoiceReject");
        var elapsed = new Stopwatch();
        yes.Click += (_, _) => { Choice = !timed || elapsed.Elapsed < TimeSpan.FromSeconds(10); DialogResult = Choice; };
        no.Click += (_, _) => { Choice = false; DialogResult = false; };
        buttons.Children.Add(yes);
        buttons.Children.Add(no);
        panel.Children.Add(buttons);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key == System.Windows.Input.Key.Escape) { args.Handled = true; Close(); }
        };
        ContentRendered += (_, _) => no.Focus();
        if (!timed) return;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        void Update()
        {
            var remaining = Math.Max(0, 10 - elapsed.Elapsed.TotalSeconds);
            countdown.Text = LanguageRuntime.Format("ScaleConfirm.Countdown", (int)Math.Ceiling(remaining));
            if (remaining <= 0) DialogResult = false;
        }
        timer.Tick += (_, _) => Update();
        ContentRendered += (_, _) => { elapsed.Start(); Update(); timer.Start(); };
        Closed += (_, _) => timer.Stop();
    }

    private static Button MakeButton(string label, string id)
    {
        var button = new Button { Content = label, Margin = new Thickness(4), Padding = new Thickness(12, 8, 12, 8),
            Style = new Style(typeof(Button)), FontSize = 14, MinHeight = 36 };
        AutomationProperties.SetName(button, label);
        AutomationProperties.SetAutomationId(button, id);
        return button;
    }
}
