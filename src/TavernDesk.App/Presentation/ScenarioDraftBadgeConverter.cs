using System.Globalization;
using System.Windows;
using System.Windows.Data;
using TavernDesk.Core.Models;

namespace TavernDesk.App.Presentation;

public sealed class ScenarioDraftBadgeConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var draft = values.Length == 2 && values[0] is string id
            && values[1] is IReadOnlyList<CampaignScenarioEditDraft> drafts
            ? drafts.FirstOrDefault(item => item.Id == id) : null;
        return parameter as string == "NoDraft" ? draft is null ? Visibility.Visible : Visibility.Collapsed
            : parameter as string == "Draft" ? (object?)draft ?? DependencyProperty.UnsetValue
            : draft is not null ? Visibility.Visible : Visibility.Collapsed;
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
