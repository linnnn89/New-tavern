using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace TavernDesk.App.Presentation;

/// <summary>Two persistent panes: side by side, optionally stacked, or selected by a compact tab row.</summary>
public sealed class ResponsivePanes : Decorator
{
    private readonly ResponsiveViewport _viewport = new();
    private readonly Grid _grid = new();
    private readonly WrapPanel _tabs = new();
    private readonly RadioButton _firstTab = new();
    private readonly RadioButton _secondTab = new();
    private bool _sideBySide = true;
    private bool _stacked;
    private bool _secondSelected;
    private string? _appliedMode;

    public static readonly DependencyProperty FirstProperty = Register(nameof(First), typeof(FrameworkElement), null);
    public static readonly DependencyProperty SecondProperty = Register(nameof(Second), typeof(FrameworkElement), null);
    public static readonly DependencyProperty FirstHeaderProperty = Register(nameof(FirstHeader), typeof(string), "");
    public static readonly DependencyProperty SecondHeaderProperty = Register(nameof(SecondHeader), typeof(string), "");
    public static readonly DependencyProperty ReferenceWidthProperty = Register(nameof(ReferenceWidth), typeof(double), 800d);
    public static readonly DependencyProperty StackedReferenceHeightProperty = Register(nameof(StackedReferenceHeight), typeof(double), double.PositiveInfinity);
    public static readonly DependencyProperty FirstWidthProperty = Register(nameof(FirstWidth), typeof(GridLength), new GridLength(1, GridUnitType.Star));
    public static readonly DependencyProperty SecondWidthProperty = Register(nameof(SecondWidth), typeof(GridLength), new GridLength(1, GridUnitType.Star));
    public static readonly DependencyProperty ShowFirstPaneProperty = Register(nameof(ShowFirstPane), typeof(bool), true);

    private static DependencyProperty Register(string name, Type type, object? value) =>
        DependencyProperty.Register(name, type, typeof(ResponsivePanes),
            new FrameworkPropertyMetadata(value, OnConfigurationChanged));

    public FrameworkElement? First { get => (FrameworkElement?)GetValue(FirstProperty); set => SetValue(FirstProperty, value); }
    public FrameworkElement? Second { get => (FrameworkElement?)GetValue(SecondProperty); set => SetValue(SecondProperty, value); }
    public string FirstHeader { get => (string)GetValue(FirstHeaderProperty); set => SetValue(FirstHeaderProperty, value); }
    public string SecondHeader { get => (string)GetValue(SecondHeaderProperty); set => SetValue(SecondHeaderProperty, value); }
    public double ReferenceWidth { get => (double)GetValue(ReferenceWidthProperty); set => SetValue(ReferenceWidthProperty, value); }
    public double StackedReferenceHeight { get => (double)GetValue(StackedReferenceHeightProperty); set => SetValue(StackedReferenceHeightProperty, value); }
    public GridLength FirstWidth { get => (GridLength)GetValue(FirstWidthProperty); set => SetValue(FirstWidthProperty, value); }
    public GridLength SecondWidth { get => (GridLength)GetValue(SecondWidthProperty); set => SetValue(SecondWidthProperty, value); }
    public bool ShowFirstPane { get => (bool)GetValue(ShowFirstPaneProperty); set => SetValue(ShowFirstPaneProperty, value); }
    public string LayoutMode => !ShowFirstPane ? "Single" : _sideBySide ? "Columns" : _stacked ? "Rows" : "Tabs";

    public ResponsivePanes()
    {
        Child = _viewport;
        _viewport.Child = _grid;
        _viewport.SetResourceReference(ResponsiveViewport.ReferenceFontSizeProperty, "InterfaceFontSize");
        _viewport.AvailableSpaceChanged += (_, _) => UpdateLayoutMode();
        for (var i = 0; i < 3; i++) _grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (var i = 0; i < 4; i++) _grid.RowDefinitions.Add(new RowDefinition());
        _grid.Children.Add(_tabs);
        Grid.SetColumnSpan(_tabs, 3);
        _tabs.Children.Add(_firstTab);
        _tabs.Children.Add(_secondTab);
        foreach (var tab in new[] { _firstTab, _secondTab })
        {
            tab.SetResourceReference(ContentControl.ContentTemplateProperty, "WrappingActionText");
            tab.Margin = new Thickness(0, 0, 16, 8);
            tab.SetResourceReference(Control.FontSizeProperty, "InterfaceFontSize");
        }
        _firstTab.SetBinding(ContentControl.ContentProperty, new Binding(nameof(FirstHeader)) { Source = this });
        _secondTab.SetBinding(ContentControl.ContentProperty, new Binding(nameof(SecondHeader)) { Source = this });
        _firstTab.Click += (_, _) => SelectPane(false);
        _secondTab.Click += (_, _) => SelectPane(true);
    }

    public void SelectPane(bool second)
    {
        _secondSelected = second;
        ApplyLayoutMode();
    }

    private static void OnConfigurationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var panes = (ResponsivePanes)d;
        if (e.Property == FirstProperty || e.Property == SecondProperty)
        {
            if (e.OldValue is FrameworkElement oldPane) panes._grid.Children.Remove(oldPane);
            if (e.NewValue is FrameworkElement newPane) panes._grid.Children.Add(newPane);
        }
        panes._appliedMode = null;
        panes.UpdateLayoutMode();
    }

    private void UpdateLayoutMode()
    {
        var space = _viewport.AvailableSize;
        var font = _viewport.FontFactor;
        _sideBySide = ResponsiveFit.Fits(space.Width, space.Height, ReferenceWidth * font, 0, _sideBySide);
        _stacked = ResponsiveFit.Fits(space.Width, space.Height, 0, StackedReferenceHeight * font, _stacked);
        ApplyLayoutMode();
    }

    private void ApplyLayoutMode()
    {
        if (First is null || Second is null) return;
        var mode = LayoutMode;
        var tabs = mode == "Tabs";
        var secondVisible = !tabs || _secondSelected;
        _tabs.Visibility = tabs ? Visibility.Visible : Visibility.Collapsed;
        // Keep hidden panes measured and parented: their selection, scroll viewer and drafts survive.
        var firstVisibility = !ShowFirstPane ? Visibility.Collapsed : tabs && _secondSelected ? Visibility.Hidden : Visibility.Visible;
        var secondVisibility = secondVisible ? Visibility.Visible : Visibility.Hidden;
        if ((First.IsKeyboardFocusWithin && firstVisibility != Visibility.Visible)
            || (Second.IsKeyboardFocusWithin && secondVisibility != Visibility.Visible))
            (secondVisible ? _secondTab : _firstTab).Focus();
        First.Visibility = firstVisibility;
        Second.Visibility = secondVisibility;
        _firstTab.IsChecked = !_secondSelected;
        _secondTab.IsChecked = _secondSelected;
        if (_appliedMode == mode) return;
        _appliedMode = mode;
        _grid.RowDefinitions[0].Height = GridLength.Auto;
        _grid.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Star);
        _grid.RowDefinitions[2].Height = new GridLength(mode == "Rows" ? 14 : 0);
        _grid.RowDefinitions[3].Height = mode == "Rows" ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        _grid.ColumnDefinitions[0].Width = mode == "Columns" ? FirstWidth : new GridLength(1, GridUnitType.Star);
        _grid.ColumnDefinitions[1].Width = new GridLength(mode == "Columns" ? 14 : 0);
        _grid.ColumnDefinitions[2].Width = mode == "Columns" ? SecondWidth : new GridLength(0);
        Grid.SetRow(First, 1);
        Grid.SetColumn(First, 0);
        Grid.SetRow(Second, mode == "Rows" ? 3 : 1);
        Grid.SetColumn(Second, mode == "Columns" ? 2 : 0);
    }
}
