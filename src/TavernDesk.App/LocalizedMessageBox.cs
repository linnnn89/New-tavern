using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using TavernDesk.App.Localization;

namespace TavernDesk.App;

public enum DialogButtonRole
{
    Primary,
    Secondary,
    Tonal,
    Destructive
}

public sealed record DialogAction(
    string Label,
    MessageBoxResult Result,
    DialogButtonRole Role = DialogButtonRole.Secondary,
    bool IsDefault = false,
    bool IsCancel = false,
    string? AutomationId = null);

public static class LocalizedMessageBox
{
    public static MessageBoxResult Show(
        string message,
        string title,
        MessageBoxButton buttons,
        MessageBoxImage image) =>
        Show(null, message, title, buttons, image, MessageBoxResult.None);

    public static MessageBoxResult Show(
        Window? owner,
        string message,
        string title,
        MessageBoxButton buttons,
        MessageBoxImage image) =>
        Show(owner, message, title, buttons, image, MessageBoxResult.None);

    public static MessageBoxResult Show(
        Window? owner,
        string message,
        string title,
        MessageBoxButton buttons,
        MessageBoxImage image,
        MessageBoxResult defaultResult)
    {
        try
        {
            var dialog = new LocalizedMessageBoxWindow(
                message,
                title,
                buttons,
                image,
                defaultResult);
            if (owner is not null)
            {
                dialog.Owner = owner;
                dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }

            dialog.ShowDialog();
            return dialog.Result;
        }
        catch
        {
            // Keep startup and fatal-error reporting available even if the
            // application resource graph itself is what failed to load.
            return owner is null
                ? MessageBox.Show(
                    message,
                    title,
                    buttons,
                    image,
                    defaultResult)
                : MessageBox.Show(
                    owner,
                    message,
                    title,
                    buttons,
                    image,
                    defaultResult);
        }
    }

    public static MessageBoxResult Show(
        string message,
        string title,
        IReadOnlyList<DialogAction> actions,
        MessageBoxImage image,
        MessageBoxResult fallbackResult = MessageBoxResult.Cancel) =>
        Show(null, message, title, actions, image, fallbackResult);

    public static MessageBoxResult Show(
        Window? owner,
        string message,
        string title,
        IReadOnlyList<DialogAction> actions,
        MessageBoxImage image,
        MessageBoxResult fallbackResult = MessageBoxResult.Cancel)
    {
        try
        {
            var dialog = new LocalizedMessageBoxWindow(
                message,
                title,
                actions,
                image,
                fallbackResult);
            if (owner is not null)
            {
                dialog.Owner = owner;
                dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }

            dialog.ShowDialog();
            return dialog.Result;
        }
        catch
        {
            return fallbackResult;
        }
    }

    private sealed class LocalizedMessageBoxWindow : Window
    {
        public LocalizedMessageBoxWindow(
            string message,
            string title,
            MessageBoxButton buttons,
            MessageBoxImage image,
            MessageBoxResult defaultResult)
        {
            var (actions, fallback) = MapStandardButtons(buttons, defaultResult);
            InitializeDialog(message, title, actions, image, fallback);
        }

        public LocalizedMessageBoxWindow(
            string message,
            string title,
            IReadOnlyList<DialogAction> actions,
            MessageBoxImage image,
            MessageBoxResult fallbackResult)
        {
            ArgumentNullException.ThrowIfNull(actions);
            if (actions.Count == 0)
            {
                actions = [new DialogAction(LanguageRuntime.GetString("Common.OK"), MessageBoxResult.OK, DialogButtonRole.Primary, IsDefault: true)];
            }
            InitializeDialog(message, title, actions, image, fallbackResult);
        }

        private void InitializeDialog(
            string message,
            string title,
            IReadOnlyList<DialogAction> actions,
            MessageBoxImage image,
            MessageBoxResult fallbackResult)
        {
            ArgumentNullException.ThrowIfNull(message);
            ArgumentNullException.ThrowIfNull(title);

            Title = title;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            Result = fallbackResult;

            var root = new Grid();
            root.SetResourceReference(
                FrameworkElement.StyleProperty,
                "InterfaceScaleRootStyle");
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var body = BuildBody(message, image);
            Grid.SetRow(body, 0);
            root.Children.Add(body);

            var footer = BuildFooter(actions);
            Grid.SetRow(footer, 1);
            root.Children.Add(footer);

            Content = root;
        }

        public MessageBoxResult Result { get; private set; }

        private static FrameworkElement BuildBody(
            string message,
            MessageBoxImage image)
        {
            var body = new Grid
            {
                Margin = new Thickness(24)
            };
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = BuildIcon(image);
            if (icon is not null)
            {
                Grid.SetColumn(icon, 0);
                body.Children.Add(icon);
            }

            var messageText = new TextBlock
            {
                MinWidth = 360,
                MaxWidth = 540,
                VerticalAlignment = VerticalAlignment.Center,
                Text = message,
                TextWrapping = TextWrapping.Wrap
            };
            messageText.SetResourceReference(
                TextBlock.ForegroundProperty,
                "TextBrush");
            if (icon is not null)
            {
                messageText.Margin = new Thickness(18, 0, 0, 0);
            }

            var scrollViewer = new ScrollViewer
            {
                Content = messageText,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 420,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            Grid.SetColumn(scrollViewer, 1);
            body.Children.Add(scrollViewer);
            return body;
        }

        private static Border? BuildIcon(MessageBoxImage image)
        {
            if (image == MessageBoxImage.None)
            {
                return null;
            }

            var (glyph, foregroundKey) = image switch
            {
                MessageBoxImage.Error => ("×", "DangerBrush"),
                MessageBoxImage.Warning => ("!", "DangerBrush"),
                MessageBoxImage.Question => ("?", "AccentBrush"),
                MessageBoxImage.Information => ("i", "AccentBrush"),
                _ => ("i", "AccentBrush")
            };

            var glyphText = new TextBlock
            {
                FontSize = 22,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Text = glyph
            };
            glyphText.SetResourceReference(
                TextBlock.ForegroundProperty,
                foregroundKey);

            var badge = new Border
            {
                Width = 44,
                Height = 44,
                CornerRadius = new CornerRadius(22),
                Child = glyphText,
                VerticalAlignment = VerticalAlignment.Top
            };
            badge.SetResourceReference(
                Border.BackgroundProperty,
                "AccentSoftBrush");
            return badge;
        }

        private Border BuildFooter(IReadOnlyList<DialogAction> actions)
        {
            var panel = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                Orientation = Orientation.Horizontal
            };

            Button? defaultButton = null;
            foreach (var action in actions)
            {
                var button = new Button
                {
                    MinWidth = 104,
                    Margin = panel.Children.Count == 0
                        ? new Thickness(0)
                        : new Thickness(10, 0, 0, 0),
                    Content = action.Label,
                    IsCancel = action.IsCancel,
                    IsDefault = action.IsDefault
                };
                var autoId = action.AutomationId ?? $"LocalizedMessageBox.{action.Result}";
                AutomationProperties.SetAutomationId(button, autoId);
                AutomationProperties.SetName(button, action.Label);

                var styleKey = action.Role switch
                {
                    DialogButtonRole.Destructive => "DestructiveButtonStyle",
                    DialogButtonRole.Primary => "PrimaryButtonStyle",
                    DialogButtonRole.Tonal => "TonalButtonStyle",
                    _ => "SecondaryButtonStyle"
                };
                button.SetResourceReference(FrameworkElement.StyleProperty, styleKey);

                button.Click += (_, _) =>
                {
                    Result = action.Result;
                    Close();
                };
                panel.Children.Add(button);
                if (action.IsDefault)
                {
                    defaultButton = button;
                }
            }

            if (defaultButton is not null)
            {
                Loaded += (_, _) => defaultButton.Focus();
            }

            var footer = new Border
            {
                BorderThickness = new Thickness(0, 1, 0, 0),
                Child = panel,
                Padding = new Thickness(20, 14, 20, 14)
            };
            footer.SetResourceReference(
                Border.BackgroundProperty,
                "SurfaceAltBrush");
            footer.SetResourceReference(
                Border.BorderBrushProperty,
                "BorderBrush");
            return footer;
        }

        private static (IReadOnlyList<DialogAction> Actions, MessageBoxResult Fallback) MapStandardButtons(
            MessageBoxButton buttons,
            MessageBoxResult defaultResult)
        {
            var fallback = ResolveFallback(buttons);
            var results = ResultsFor(buttons);
            var resolvedDefault = ResolveDefault(results, defaultResult);

            var actions = new List<DialogAction>(results.Count);
            foreach (var r in results)
            {
                var isDefault = r == resolvedDefault;
                var isCancel = r == MessageBoxResult.Cancel;
                var role = isDefault ? DialogButtonRole.Primary : DialogButtonRole.Secondary;
                actions.Add(new DialogAction(
                    LabelFor(r),
                    r,
                    role,
                    IsDefault: isDefault,
                    IsCancel: isCancel,
                    AutomationId: $"LocalizedMessageBox.{r}"));
            }
            return (actions, fallback);
        }

        private static IReadOnlyList<MessageBoxResult> ResultsFor(
            MessageBoxButton buttons) =>
            buttons switch
            {
                MessageBoxButton.OK => [MessageBoxResult.OK],
                MessageBoxButton.OKCancel =>
                    [MessageBoxResult.OK, MessageBoxResult.Cancel],
                MessageBoxButton.YesNo =>
                    [MessageBoxResult.Yes, MessageBoxResult.No],
                MessageBoxButton.YesNoCancel =>
                    [
                        MessageBoxResult.Yes,
                        MessageBoxResult.No,
                        MessageBoxResult.Cancel
                    ],
                _ => throw new ArgumentOutOfRangeException(
                    nameof(buttons),
                    buttons,
                    null)
            };

        private static MessageBoxResult ResolveDefault(
            IReadOnlyList<MessageBoxResult> results,
            MessageBoxResult requested) =>
            requested != MessageBoxResult.None && results.Contains(requested)
                ? requested
                : results[0];

        private static MessageBoxResult ResolveFallback(
            MessageBoxButton buttons) =>
            buttons switch
            {
                MessageBoxButton.OK => MessageBoxResult.OK,
                MessageBoxButton.OKCancel => MessageBoxResult.Cancel,
                MessageBoxButton.YesNo => MessageBoxResult.No,
                MessageBoxButton.YesNoCancel => MessageBoxResult.Cancel,
                _ => MessageBoxResult.None
            };

        private static string LabelFor(MessageBoxResult result) =>
            LanguageRuntime.GetString(result switch
            {
                MessageBoxResult.OK => "Common.OK",
                MessageBoxResult.Yes => "Common.Yes",
                MessageBoxResult.No => "Common.No",
                MessageBoxResult.Cancel => "Common.Cancel",
                _ => throw new ArgumentOutOfRangeException(
                    nameof(result),
                    result,
                    null)
            });
    }
}
