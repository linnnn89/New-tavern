using System.Globalization;
using System.Windows;
using System.Windows.Data;
using TavernDesk.Core.Models;

namespace TavernDesk.App.Presentation;

public sealed class ScenarioDraftBadgeConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length == 2 && values[0] is string id && values[1] is IReadOnlyList<CampaignScenarioEditDraft> drafts
        && drafts.Any(draft => draft.Id == id) ? Visibility.Visible : Visibility.Collapsed;
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
