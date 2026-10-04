using System.Windows;

namespace TavernDesk.App.Presentation;

public static class InspectorNavigation
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.RegisterAttached(
        "Label", typeof(string), typeof(InspectorNavigation), new PropertyMetadata(string.Empty));

    public static string GetLabel(DependencyObject element) => (string)element.GetValue(LabelProperty);
    public static void SetLabel(DependencyObject element, string value) => element.SetValue(LabelProperty, value);
}
