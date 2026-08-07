using System.Text.Encodings.Web;
using System.Text.Json;

namespace TavernDesk.Infrastructure.Campaigns;

/// <summary>
/// Keeps the character-card payload sent to campaign models deliberately small
/// and stable. The source card remains untouched; this is only a prompt view.
/// </summary>
internal static class CampaignCharacterPromptFormatter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Format(string displayName, string? snapshotJson)
    {
        var name = displayName ?? string.Empty;
        var description = string.Empty;
        var personality = string.Empty;
        var dialogueExamples = string.Empty;

        if (!string.IsNullOrWhiteSpace(snapshotJson))
        {
            try
            {
                using var document = JsonDocument.Parse(snapshotJson);
                var root = document.RootElement;
                var identity = Property(root, "identity");
                var behavior = Property(root, "behavior");
                name = ReadString(identity, "name", name);
                description = ReadString(identity, "description");
                personality = ReadString(identity, "personality");
                dialogueExamples = ReadString(behavior, "dialogue_examples");

                // Accept the four-field shape as well as older snapshots while
                // never copying any other card property into the prompt.
                name = ReadString(root, "name", name);
                description = ReadString(root, "description", description);
                personality = ReadString(root, "personality", personality);
                dialogueExamples = ReadString(
                    root,
                    "dialogue_examples",
                    dialogueExamples);
            }
            catch (JsonException)
            {
                // A malformed legacy snapshot must not reintroduce the raw card.
            }
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            name = displayName ?? string.Empty;
        }

        return JsonSerializer.Serialize(
            new
            {
                name,
                description,
                personality,
                dialogue_examples = dialogueExamples
            },
            JsonOptions);
    }

    private static JsonElement Property(JsonElement source, string name) =>
        source.ValueKind == JsonValueKind.Object
        && source.TryGetProperty(name, out var value)
            ? value
            : default;

    private static string ReadString(
        JsonElement source,
        string name,
        string fallback = "") =>
        source.ValueKind == JsonValueKind.Object
        && source.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;
}
