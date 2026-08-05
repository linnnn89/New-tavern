namespace TavernDesk.Core.Models;

public enum PresetScopeKind
{
    Global,
    Character,
    Conversation
}

public sealed class PromptPreset
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新预设";
    public string Description { get; set; } = string.Empty;
    public string OverlayJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed record PresetMount(
    PresetScopeKind ScopeKind,
    string ScopeId,
    string PresetId,
    int SortIndex,
    bool IsEnabled);

public sealed record ResolvedPreset(
    string OverlayJson,
    string? SystemPrompt,
    IReadOnlyList<string> Diagnostics);
