using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace TavernDesk.App.Presentation;

/// <summary>
/// Keeps nested WPF ScrollViewers usable with a mouse wheel. The innermost
/// viewer consumes the wheel while it can move; at either edge the event is
/// routed to the next parent viewer instead of stopping at the child.
/// </summary>
public static class ScrollViewerWheelRouter
{
    private static bool _registered;

    public static void Register()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;
        EventManager.RegisterClassHandler(
            typeof(ScrollViewer),
            UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnPreviewMouseWheel),
            handledEventsToo: true);
    }

    private static void OnPreviewMouseWheel(
        object sender,
        MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not ScrollViewer current)
        {
            return;
        }

        var nearest = FindNearestScrollViewer(e.OriginalSource as DependencyObject);
        if (nearest is null || !ReferenceEquals(nearest, current))
        {
            return;
        }

        for (var viewer = nearest;
             viewer is not null;
             viewer = FindParentScrollViewer(viewer))
        {
            if (!TryScroll(viewer, e.Delta))
            {
                continue;
            }

            e.Handled = true;
            return;
        }
    }

    private static bool TryScroll(ScrollViewer viewer, int delta)
    {
        if (delta == 0 || viewer.ScrollableHeight <= 0)
        {
            return false;
        }

        var before = viewer.VerticalOffset;
        if (delta > 0)
        {
            viewer.LineUp();
        }
        else
        {
            viewer.LineDown();
        }

        return Math.Abs(viewer.VerticalOffset - before) > 0.01;
    }

    private static ScrollViewer? FindNearestScrollViewer(DependencyObject? source)
    {
        for (var current = source;
             current is not null;
             current = GetParent(current))
        {
            if (current is ScrollViewer viewer)
            {
                return viewer;
            }
        }

        return null;
    }

    private static ScrollViewer? FindParentScrollViewer(ScrollViewer viewer)
    {
        for (var current = GetParent(viewer);
             current is not null;
             current = GetParent(current))
        {
            if (current is ScrollViewer parent)
            {
                return parent;
            }
        }

        return null;
    }

    private static DependencyObject? GetParent(DependencyObject current) =>
        current switch
        {
            Visual or Visual3D => VisualTreeHelper.GetParent(current),
            FrameworkContentElement content => content.Parent,
            _ => null
        };
}
