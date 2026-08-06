namespace TavernDesk.Core.Models;

public sealed class CampaignScenario
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string WorldSetting { get; set; } = string.Empty;
    public string PublicRules { get; set; } = string.Empty;
    public string GmInstructions { get; set; } = string.Empty;
    public string OpeningSetup { get; set; } = string.Empty;
    public string OpeningNarration { get; set; } = string.Empty;
    public string LegacyExamplesArchive { get; set; } = string.Empty;
    public string SourceCardJson { get; set; } = "{}";
    public string SourceFileName { get; set; } = string.Empty;
    public string CoverPath { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed record CampaignScenarioImportResult(
    CampaignScenario Scenario,
    IReadOnlyList<string> Warnings);
