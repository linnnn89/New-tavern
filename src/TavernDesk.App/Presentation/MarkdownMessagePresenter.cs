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

    public MarkdownMessagePresenter()
    {
        Content = _root;
        InterfaceSettingsRuntime.Changed += OnThemeChanged;
        Unloaded += (_, _) => InterfaceSettingsRuntime.Changed -= OnThemeChanged;
    }

    public string MarkdownText
    {
        get => (string)GetValue(MarkdownTextProperty);
        set => SetValue(MarkdownTextProperty, value);
    }

    private void OnThemeChanged(object? sender, EventArgs args) =>
        Dispatcher.BeginInvoke(Rebuild);

    private static void OnMarkdownTextChanged(
        DependencyObject target,
        DependencyPropertyChangedEventArgs args)
    {
        if (target is MarkdownMessagePresenter presenter)
        {
            presenter.Rebuild();
        }
    }

    private void Rebuild()
    {
        _root.Children.Clear();
        var source = MarkdownText ?? string.Empty;
        if (source.Length == 0)
        {
            return;
        }

        var isDark = string.Equals(
            InterfaceSettingsRuntime.ThemeName,
            InterfaceSettingsRuntime.DarkThemeName,
            StringComparison.Ordinal);
        var inlineCodeBrush = CreateBrush(
            isDark ? Color.FromRgb(0x2B, 0x31, 0x3A) : Color.FromRgb(0xEE, 0xF2, 0xF7));
        var blockCodeBrush = CreateBrush(
            isDark ? Color.FromRgb(0x24, 0x29, 0x31) : Color.FromRgb(0xF4, 0xF6, 0xFA));
        var blockCodeBorderBrush = CreateBrush(
            isDark ? Color.FromRgb(0x3A, 0x42, 0x4E) : Color.FromRgb(0xE1, 0xE6, 0xEE));
        var quoteBrush = CreateBrush(
            isDark ? Color.FromRgb(0x8B, 0xA4, 0xC7) : Color.FromRgb(0x5B, 0x74, 0x99));

        var lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        TextBlock? paragraph = null;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph(ref paragraph);
                var code = new System.Text.StringBuilder();
                index++;
                while (index < lines.Length
                       && !lines[index].StartsWith("```", StringComparison.Ordinal))
                {
                    if (code.Length > 0)
                    {
                        code.Append('\n');
                    }

                    code.Append(lines[index]);
                    index++;
                }

                _root.Children.Add(new Border
                {
                    Margin = new Thickness(0, 8, 0, 8),
                    Padding = new Thickness(12, 10, 12, 10),
                    CornerRadius = new CornerRadius(8),
                    Background = blockCodeBrush,
                    BorderBrush = blockCodeBorderBrush,
                    BorderThickness = new Thickness(1),
                    Child = CreateMonospaceBlock(code.ToString())
                });
                continue;
            }

            paragraph ??= CreateParagraph();
            if (paragraph.Inlines.Count > 0)
            {
                paragraph.Inlines.Add(new LineBreak());
            }

            if (line.StartsWith("> ", StringComparison.Ordinal))
            {
                paragraph.Inlines.Add(new Run(line[2..])
                {
                    FontStyle = FontStyles.Italic,
                    Foreground = quoteBrush
                });
                continue;
            }

            if (line.StartsWith("- ", StringComparison.Ordinal)
                || line.StartsWith("* ", StringComparison.Ordinal))
            {
                paragraph.Inlines.Add(new Run("• " + line[2..]));
                continue;
            }

            AppendInlineMarkdown(paragraph, line, inlineCodeBrush);
        }

        FlushParagraph(ref paragraph);
    }

    private void FlushParagraph(ref TextBlock? paragraph)
    {
        if (paragraph is null)
        {
            return;
        }

        _root.Children.Add(paragraph);
        paragraph = null;
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
