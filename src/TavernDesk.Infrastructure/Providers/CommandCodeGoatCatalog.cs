using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TavernDesk.Infrastructure.Providers;

internal static class CommandCodeGoatCatalog
{
    internal const string PlanUrl = "https://commandcode.ai/docs/plans/goat";

    // The official plan table is embedded in Next.js flight strings. Decode
    // JSON only; do not execute the page's scripts or trust its endpoint URLs.
    internal static IReadOnlyList<JsonElement> ReadPlan(string html)
    {
        var payload = new StringBuilder();
        foreach (Match match in Regex.Matches(html,
                     "self\\.__next_f\\.push\\(\\[1,(\"(?:[^\"\\\\]|\\\\.)*\")\\]\\)",
                     RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)))
        {
            payload.Append(JsonSerializer.Deserialize<string>(match.Groups[1].Value));
        }

        var text = payload.ToString();
        var start = text.IndexOf("[{\"slug\":", StringComparison.Ordinal);
        if (start < 0)
            throw new InvalidDataException("无法读取 Command Code GOAT 套餐目录，已保留原模型列表。");
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(text[start..]),
            new JsonReaderOptions { MaxDepth = 64 });
        using var document = JsonDocument.ParseValue(ref reader);
        var entries = document.RootElement.EnumerateArray().Select(item => item.Clone()).ToArray();
        if (!entries.Any(entry => ReadString(entry, "minPlanName") == "GOAT"))
            throw new InvalidDataException("Command Code GOAT 套餐目录缺少套餐信息，已保留原模型列表。");
        return entries;
    }

    internal static bool Includes(IReadOnlyList<JsonElement> plan, JsonElement model)
    {
        var id = ReadString(model, "id");
        if (id is null) return false;
        var matches = plan.Where(entry => ReadString(entry, "id") == id).ToArray();
        if (matches.Length == 0)
            matches = plan.Where(entry => Slug(ReadString(entry, "slug")) == Slug(id)).ToArray();
        if (matches.Length == 0 && ReadString(model, "name") is { } name)
            matches = plan.Where(entry => ReadString(entry, "name") == name).ToArray();
        // Unknown/ambiguous models must not be advertised as part of GOAT.
        return matches.Length == 1 && ReadString(matches[0], "minPlanName") is "Go" or "GOAT";
    }

    private static string Slug(string? value) =>
        new((value ?? string.Empty).Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
