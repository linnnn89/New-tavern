using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TavernDesk.App.Presentation;

public sealed class ReferenceEqualsMultiConverter : IMultiValueConverter
{
    public object Convert(
        object[] values,
        Type targetType,
        object parameter,
        CultureInfo culture)
    {
        if (values.Length < 2
            || values[0] is null
            || values[0] == DependencyProperty.UnsetValue
            || values[1] == DependencyProperty.UnsetValue)
        {
            return false;
        }

        return ReferenceEquals(values[0], values[1]);
    }

    public object[] ConvertBack(
        object value,
        Type[] targetTypes,
        object parameter,
        CultureInfo culture) =>
        throw new NotSupportedException();
}
