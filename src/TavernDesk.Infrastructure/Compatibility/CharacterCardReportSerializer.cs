using System.Text.Json;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;

namespace TavernDesk.Infrastructure.Compatibility;

internal static class CharacterCardReportSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false
    };

    public static string Write(CharacterCardImportReport report) =>
        JsonSerializer.Serialize(report, Options);

    public static CharacterCardImportReport? TryRead(string json)
    {
        try
        {
            var report = JsonSerializer.Deserialize<CharacterCardImportReport>(json, Options);
            return report is not null
                && !string.IsNullOrWhiteSpace(report.FormatName)
                && report.UnknownFieldPaths is not null
                && report.Resources is not null
                && report.Warnings is not null
                    ? report
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
