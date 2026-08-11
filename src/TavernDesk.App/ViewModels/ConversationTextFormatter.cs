using System.Globalization;
using System.Text.RegularExpressions;
using TavernDesk.App.Localization;

namespace TavernDesk.App.ViewModels;

internal static partial class ConversationTextFormatter
{
    public static string Preview(string content, int maximumTextElements = 8)
    {
        var normalized = NormalizePreview(content);

        var elementOffsets = StringInfo.ParseCombiningCharacters(normalized);
        if (elementOffsets.Length <= maximumTextElements)
        {
            return normalized;
        }

        return normalized[..elementOffsets[maximumTextElements]] + "…";
    }

    public static string NormalizePreview(string content)
    {
        var normalized = WhitespacePattern().Replace(content ?? string.Empty, " ").Trim();
        return normalized.Length == 0
            ? LanguageRuntime.GetString("Conversation.New")
            : normalized;
    }

    public static string FriendlyTime(DateTimeOffset value)
    {
        var local = value.ToLocalTime();
        var now = DateTimeOffset.Now;
        if (local.Date == now.Date)
        {
            return local.ToString("HH:mm");
        }

        if (local.Date == now.Date.AddDays(-1))
        {
            return LanguageRuntime.GetString("Conversation.Yesterday");
        }

        return local.Year == now.Year
            ? local.ToString("MM-dd")
            : local.ToString("yyyy-MM-dd");
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();
}
