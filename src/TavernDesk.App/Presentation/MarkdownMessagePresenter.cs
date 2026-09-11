using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using TavernDesk.App.Services;

namespace TavernDesk.App.Presentation;

public sealed class MarkdownMessagePresenter : UserControl
{
    public static readonly DependencyProperty MarkdownTextProperty =
        DependencyProperty.Register(
            nameof(MarkdownText),
            typeof(string),
            typeof(MarkdownMessagePresenter),
            new PropertyMetadata(string.Empty, OnMarkdownTextChanged));

    private readonly StackPanel _root = new();
    private bool _isWatchingInterfaceSettings;
    private readonly List<MarkdownMessageBlock> _blocks = new();

    public MarkdownMessagePresenter()
    {
        Content = _root;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public string MarkdownText
    {
        get => (string)GetValue(MarkdownTextProperty);
        set => SetValue(MarkdownTextProperty, value);
    }

    private void OnThemeChanged(object? sender, EventArgs args) =>
        Dispatcher.BeginInvoke(Rebuild);

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        // Virtualized message controls can load more than once. Pair the global
        // subscription with each loaded lifetime so reloaded controls still
        // respond without accumulating duplicate handlers.
        if (!_isWatchingInterfaceSettings)
        {
            InterfaceSettingsRuntime.Changed += OnThemeChanged;
            _isWatchingInterfaceSettings = true;
        }

        Rebuild();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (!_isWatchingInterfaceSettings)
        {
            return;
        }

        // The event source is static, so releasing the handler here prevents
        // unloaded message controls from being retained for the app lifetime.
        InterfaceSettingsRuntime.Changed -= OnThemeChanged;
        _isWatchingInterfaceSettings = false;
    }

    private static void OnMarkdownTextChanged(
        DependencyObject target,
        DependencyPropertyChangedEventArgs args)
    {
        if (target is MarkdownMessagePresenter presenter)
        {
            presenter.UpdateMarkdown();
        }
    }

    private void Rebuild()
    {
        _blocks.Clear();
        _root.Children.Clear();
        UpdateMarkdown();
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs args)
    {
        base.OnPropertyChanged(args);
        if (args.Property == FontSizeProperty && _root is not null) Rebuild();
    }

    private void UpdateMarkdown()
    {
        var blocks = MarkdownMessageBlocks.Parse(MarkdownText ?? string.Empty);
        var isDark = string.Equals(InterfaceSettingsRuntime.ThemeName,
            InterfaceSettingsRuntime.DarkThemeName, StringComparison.Ordinal);
        var inlineCodeBrush = CreateBrush(isDark ? Color.FromRgb(0x2B, 0x31, 0x3A) : Color.FromRgb(0xEE, 0xF2, 0xF7));
        var blockCodeBrush = CreateBrush(isDark ? Color.FromRgb(0x24, 0x29, 0x31) : Color.FromRgb(0xF4, 0xF6, 0xFA));
        var blockCodeBorderBrush = CreateBrush(isDark ? Color.FromRgb(0x3A, 0x42, 0x4E) : Color.FromRgb(0xE1, 0xE6, 0xEE));
        var quoteBrush = CreateBrush(isDark ? Color.FromRgb(0x8B, 0xA4, 0xC7) : Color.FromRgb(0x5B, 0x74, 0x99));

        for (var index = 0; index < blocks.Count; index++)
        {
            var block = blocks[index];
            var old = index < _blocks.Count ? _blocks[index] : (MarkdownMessageBlock?)null;
            if (old == block) continue;
            if (old is { } previous && previous.IsCode == block.IsCode)
            {
                if (block.IsCode)
                {
                    UpdateCodeBlock((Border)_root.Children[index], block.Text);
                    continue;
                }
                if (previous.Text == block.Text && previous.ContinuesParagraph == block.ContinuesParagraph)
                {
                    ((TextBlock)_root.Children[index]).Margin = ParagraphMargin(block.EndsParagraph);
                    continue;
                }
            }

            FrameworkElement element;
            if (block.IsCode)
            {
                var border = new Border
                {
                    Margin = new Thickness(0, 8, 0, 8), Padding = new Thickness(12, 10, 12, 10),
                    CornerRadius = new CornerRadius(8), Background = blockCodeBrush,
                    BorderBrush = blockCodeBorderBrush, BorderThickness = new Thickness(1),
                    Child = new StackPanel()
                };
                UpdateCodeBlock(border, block.Text);
                element = border;
            }
            else
            {
                var paragraph = CreateParagraph();
                paragraph.Margin = ParagraphMargin(block.EndsParagraph);
                // A continuation beginning with a blank line must retain that line.
                if (block.ContinuesParagraph) paragraph.Inlines.Add(new Run(string.Empty));
                foreach (var line in block.Text.Split('\n'))
                {
                    if (paragraph.Inlines.Count > 0) paragraph.Inlines.Add(new LineBreak());
                    if (line.StartsWith("> ", StringComparison.Ordinal))
                        paragraph.Inlines.Add(new Run(line[2..]) { FontStyle = FontStyles.Italic, Foreground = quoteBrush });
                    else if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
                        paragraph.Inlines.Add(new Run("• " + line[2..]));
                    else AppendInlineMarkdown(paragraph, line, inlineCodeBrush);
                }
                // The first line continues the previous group's layout, not an extra line.
                if (block.ContinuesParagraph && paragraph.Inlines.FirstInline is Run first)
                {
                    var leadingBreak = first.NextInline;
                    paragraph.Inlines.Remove(first);
                    if (leadingBreak is LineBreak) paragraph.Inlines.Remove(leadingBreak);
                    if (paragraph.Inlines.Count == 0) paragraph.Inlines.Add(new Run(string.Empty));
                }
                element = paragraph;
            }
            if (index < _root.Children.Count) _root.Children.RemoveAt(index);
            _root.Children.Insert(index, element);
        }
        while (_root.Children.Count > blocks.Count) _root.Children.RemoveAt(_root.Children.Count - 1);
        _blocks.Clear();
        _blocks.AddRange(blocks);
    }

    private static Thickness ParagraphMargin(bool last) => new(0, 0, 0, last ? 6 : 0);

    private void UpdateCodeBlock(Border border, string text)
    {
        var panel = (StackPanel)border.Child;
        var lines = text.Split('\n');
        var count = (lines.Length + MarkdownMessageBlocks.LinesPerBlock - 1) / MarkdownMessageBlocks.LinesPerBlock;
        for (var index = 0; index < count; index++)
        {
            var start = index * MarkdownMessageBlocks.LinesPerBlock;
            var content = string.Join('\n', lines, start, Math.Min(MarkdownMessageBlocks.LinesPerBlock, lines.Length - start));
            if (index < panel.Children.Count)
            {
                var existing = (TextBlock)panel.Children[index];
                if (existing.Text != content) existing.Text = content;
            }
            else panel.Children.Add(CreateMonospaceBlock(content));
        }
        while (panel.Children.Count > count) panel.Children.RemoveAt(panel.Children.Count - 1);
    }

    private TextBlock CreateParagraph()
    {
        var fontSize = FontSize > 0 ? FontSize : 14;
        return new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            LineHeight = Math.Round(fontSize * 1.45, 1),
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            Margin = new Thickness(0, 0, 0, 6)
        };
    }

    private TextBlock CreateMonospaceBlock(string text)
    {
        var fontSize = FontSize > 0 ? FontSize : 14;
        return new TextBlock
        {
            Text = text,
            FontFamily = MonoFont(),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = Math.Round(fontSize * 1.35, 1),
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight
        };
    }

    private static void AppendInlineMarkdown(
        TextBlock paragraph,
        string line,
        Brush codeBrush)
    {
        var remaining = line;
        while (remaining.Length > 0)
        {
            var bold = remaining.IndexOf("**", StringComparison.Ordinal);
            var code = remaining.IndexOf('`');
            var next = MinPositive(bold, code);
            if (next < 0)
            {
                paragraph.Inlines.Add(new Run(remaining));
                return;
            }

            if (next > 0)
            {
                paragraph.Inlines.Add(new Run(remaining[..next]));
            }

            if (bold == next)
            {
                var end = remaining.IndexOf("**", next + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    paragraph.Inlines.Add(new Run(remaining[next..]));
                    return;
                }

                paragraph.Inlines.Add(new Run(remaining[(next + 2)..end])
                {
                    FontWeight = FontWeights.SemiBold
                });
                remaining = remaining[(end + 2)..];
                continue;
            }

            var codeEnd = remaining.IndexOf('`', next + 1);
            if (codeEnd < 0)
            {
                paragraph.Inlines.Add(new Run(remaining[next..]));
                return;
            }

            paragraph.Inlines.Add(new Run(remaining[(next + 1)..codeEnd])
            {
                FontFamily = MonoFont(),
                Background = codeBrush
            });
            remaining = remaining[(codeEnd + 1)..];
        }
    }

    private static FontFamily MonoFont() =>
        new("Cascadia Mono, Consolas, Courier New");

    private static SolidColorBrush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static int MinPositive(params int[] values)
    {
        var best = -1;
        foreach (var value in values)
        {
            if (value < 0)
            {
                continue;
            }

            if (best < 0 || value < best)
            {
                best = value;
            }
        }

        return best;
    }
}
