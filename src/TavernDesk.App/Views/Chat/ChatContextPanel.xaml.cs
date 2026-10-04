using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TavernDesk.App.ViewModels;

namespace TavernDesk.App.Views.Chat;

public partial class ChatContextPanel : UserControl
{
    private ChatViewModel? _model;
    public ChatContextPanel()
    {
        InitializeComponent();
        Loaded += (_, _) => Observe();
        DataContextChanged += (_, _) => Observe();
        Unloaded += (_, _) => { if (_model is not null) _model.PropertyChanged -= Changed; _model = null; };
    }

    private void Observe()
    {
        if (_model is not null) _model.PropertyChanged -= Changed;
        _model = DataContext as ChatViewModel;
        if (_model is not null) _model.PropertyChanged += Changed;
        RenderBudget();
    }

    private void Changed(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ChatViewModel.TokenBudgetParts)) RenderBudget();
    }

    private void RenderBudget()
    {
        BudgetBar.Children.Clear();
        BudgetBar.ColumnDefinitions.Clear();
        BudgetDetails.Children.Clear();
        var parts = _model?.TokenBudgetParts ?? [];
        var inputTotal = parts.Where(part => part.IsInput).Sum(part => part.Tokens);
        foreach (var part in parts)
        {
            var share = part.IsInput && inputTotal > 0 ? 100d * part.Tokens / inputTotal : 0;
            var count = part.IsInput ? $"{part.Tokens:N0} · {share:0.#}%" : $"{part.Tokens:N0}";
            var title = $"{part.Title} · {count}";
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var dot = new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            dot.SetResourceReference(BackgroundProperty, part.BrushKey);
            header.Children.Add(dot);
            var label = new TextBlock { Text = part.Title, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
            Grid.SetColumn(label, 1); header.Children.Add(label);
            var number = new TextBlock { Text = count, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
            Grid.SetColumn(number, 2); header.Children.Add(number);
            Expander? detail = null;
            if (part.Segments.Count > 0)
            {
                detail = new Expander { Header = header, Margin = new Thickness(0, 2, 0, 2), Style = (Style)Resources["BudgetDetailExpander"] };
                System.Windows.Automation.AutomationProperties.SetName(detail, title);
                detail.Content = new TextBox
                {
                    Text = string.Join("\n\n", part.Segments.Select(segment => $"{segment.Title}\n{segment.Content}")),
                    IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
                    MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                };
                BudgetDetails.Children.Add(detail);
            }
            else BudgetDetails.Children.Add(new Border { Child = header, Padding = new Thickness(0, 8, 0, 8) });
            if (!part.IsInput) continue;
            var button = new Button { ToolTip = title, Padding = new Thickness(0), MinWidth = 0,
                MinHeight = 0, BorderThickness = new Thickness(0) };
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(Background)) { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            button.Template = new ControlTemplate(typeof(Button)) { VisualTree = border };
            button.SetResourceReference(BackgroundProperty, part.BrushKey);
            System.Windows.Automation.AutomationProperties.SetName(button, title);
            Grid.SetColumn(button, BudgetBar.ColumnDefinitions.Count);
            BudgetBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(part.Tokens, GridUnitType.Star) });
            button.Click += (_, _) => { if (detail is not null) { detail.IsExpanded = !detail.IsExpanded; detail.BringIntoView(); } };
            BudgetBar.Children.Add(button);
        }
    }
}
