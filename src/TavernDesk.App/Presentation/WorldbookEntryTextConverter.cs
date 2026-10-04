using System.Globalization;
using System.Windows.Data;
using TavernDesk.App.Localization;
using TavernDesk.Core.Models;

namespace TavernDesk.App.Presentation;

public sealed class WorldbookEntryTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        WorldbookContentType type => LanguageRuntime.GetString($"Worldbook.ContentType.{type}"),
        IEnumerable<string> keys => FormatKeywords(keys),
        _ => LanguageRuntime.GetString("Worldbook.KeywordsEmpty")
    };

    private static string FormatKeywords(IEnumerable<string> keys)
    {
        var text = string.Join(LanguageRuntime.GetString("Worldbook.KeywordSeparator"),
            keys.Where(key => !string.IsNullOrWhiteSpace(key)).Select(key => key.Trim()));
        return text.Length > 0 ? text : LanguageRuntime.GetString("Worldbook.KeywordsEmpty");
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
