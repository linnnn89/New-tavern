namespace TavernDesk.Core.Models;

public enum CharacterCardFormat
{
    Json,
    Png,
    Charx
}

public sealed class Character
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Personality { get; set; } = string.Empty;
    public string Scenario { get; set; } = string.Empty;
    public string FirstMessage { get; set; } = string.Empty;
    public string AvatarPath { get; set; } = string.Empty;
    public string RawCardJson { get; set; } = "{}";
    public CharacterCardFormat SourceCardFormat { get; set; } = CharacterCardFormat.Json;
    public string SourceCardPath { get; set; } = string.Empty;
    public string ImportReportJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}
