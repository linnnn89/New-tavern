using System.Globalization;
using System.Windows.Data;
using TavernDesk.Core.Models;

namespace TavernDesk.App.Presentation;

public sealed class RecentCharactersConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is IEnumerable<Character> characters
            ? characters.OrderByDescending(character => character.UpdatedAt).Take(4).ToArray()
            : Array.Empty<Character>();

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
