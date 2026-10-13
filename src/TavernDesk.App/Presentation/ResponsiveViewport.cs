using System.Windows;
using System.Windows.Controls;

namespace TavernDesk.App.Presentation;

/// <summary>
/// A finite measure boundary. Place outside scrolling content, with no MinWidth/MinHeight.
/// Reports the parent's constraint, never the child's extent or inflated ActualWidth.
/// WPF already converts system DPI and LayoutTransform; do not divide by scale again.
/// </summary>
public sealed class ResponsiveViewport : Decorator
{
    public static readonly DependencyProperty ReferenceFontSizeProperty = DependencyProperty.Register(
        nameof(ReferenceFontSize), typeof(double), typeof(ResponsiveViewport),
        new FrameworkPropertyMetadata(14d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double ReferenceFontSize
    {
        get => (double)GetValue(ReferenceFontSizeProperty);
        set => SetValue(ReferenceFontSizeProperty, value);
    }

    public Size AvailableSize { get; private set; }
    public double FontFactor => ResponsiveFit.FontFactor(ReferenceFontSize);
    public event EventHandler? AvailableSpaceChanged;
    private double _lastFontSize;

    protected override Size MeasureOverride(Size constraint)
    {
        if (double.IsFinite(constraint.Width) && double.IsFinite(constraint.Height)
            && constraint.Width > 0 && constraint.Height > 0
            && (AvailableSize != constraint || _lastFontSize != ReferenceFontSize))
        {
            AvailableSize = constraint;
            _lastFontSize = ReferenceFontSize;
            // Decide before measuring children, so old column minima cannot influence the mode.
            AvailableSpaceChanged?.Invoke(this, EventArgs.Empty);
        }

        Child?.Measure(constraint);
        var desired = Child?.DesiredSize ?? default;
        return new Size(Math.Min(constraint.Width, desired.Width), Math.Min(constraint.Height, desired.Height));
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        Child?.Arrange(new Rect(arrangeSize));
        return arrangeSize;
    }
}
