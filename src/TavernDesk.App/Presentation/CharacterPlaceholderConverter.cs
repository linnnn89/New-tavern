using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace TavernDesk.App.Presentation;

public sealed class CharacterPlaceholderConverter : IValueConverter
{
    private static readonly (string Start, string End)[] Colors =
    [
        ("#BBC8EF", "#E6DDF5"), ("#BDDCD8", "#E4EEDB"),
        ("#D0C3EA", "#F0DCEA"), ("#BBD3EA", "#DFECF5"),
        ("#DCCBDF", "#EEE3D9"), ("#C4D7BD", "#E7E8CF"),
        ("#DABFE1", "#E6DFF5"), ("#BBDADA", "#DEE5F4")
    ];

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var name = (value as string)?.Trim() ?? string.Empty;
        if (parameter as string == "Initial")
            return name.Length == 0 ? "?" : StringInfo.GetNextTextElement(name);

        // Use a stable hash so a name keeps its cover colors across app restarts.
        uint hash = 2166136261;
        foreach (var character in name) hash = unchecked((hash ^ character) * 16777619);
        var colors = Colors[hash % Colors.Length];
        var brush = new LinearGradientBrush(
            (Color)ColorConverter.ConvertFromString(colors.Start),
            (Color)ColorConverter.ConvertFromString(colors.End), 45);
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
