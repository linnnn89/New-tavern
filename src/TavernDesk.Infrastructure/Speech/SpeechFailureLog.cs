using System.Text.Json.Nodes;
using TavernDesk.Infrastructure.Diagnostics;

namespace TavernDesk.Infrastructure.Speech;

// Request content and credentials are only used in memory to remove provider echoes.
public sealed class SpeechFailureLog(ITavernDeskDiagnostics diagnostics, string text)
{
    public Dictionary<string, object?> Context { get; } = new()
    {
        ["attempt_id"] = Guid.NewGuid().ToString("N"),
        ["text_characters"] = text.Length,
        ["stage"] = "validation"
    };
    public string? Credential { private get; set; }

    public string Sanitize(string value)
    {
        if (!string.IsNullOrEmpty(Credential))
            value = value.Replace(Credential, "[REDACTED]", StringComparison.Ordinal);
        if (!string.IsNullOrEmpty(text))
        {
            value = value.Replace(text, "[STORY_OMITTED]", StringComparison.Ordinal);
            // Error messages can quote only part of the submitted text. Omit such
            // messages instead of persisting a partial story alongside the error.
            var length = Math.Min(8, text.Length);
            for (var i = 0; i <= value.Length - length; i++)
                if (text.Contains(value.Substring(i, length), StringComparison.Ordinal))
                    return "[STORY_ECHO_OMITTED]";
        }
        return value.Length > 2048 ? value[..2048] + "[TRUNCATED]" : value;
    }

    public JsonNode? SanitizeResponse(JsonNode? node, string field = "")
    {
        if (node is JsonObject obj)
        {
            var result = new JsonObject();
            foreach (var pair in obj)
                result[Sanitize(pair.Key)] = SanitizeResponse(pair.Value, pair.Key);
            return result;
        }
        if (node is JsonArray array)
            return new JsonArray(array.Select(item => SanitizeResponse(item, field)).ToArray());
        if (node is JsonValue value && value.TryGetValue<string>(out var content))
        {
            // Only diagnostic strings are retained. Input/content/unknown fields
            // keep their positions in the response but never their string values.
            return JsonValue.Create(field.ToLowerInvariant() is
                "message" or "detail" or "error" or "code" or "type" or "msg" or "loc" or "param"
                ? Sanitize(content) : "[OMITTED]");
        }
        return node?.DeepClone();
    }

    public void Write(Exception error)
    {
        Context["error_code"] = error is TavernDesk.Core.Abstractions.SpeechException speech ? speech.Code : null;
        Context["error_message"] = Sanitize(error.Message);
        Context["hresult"] = error.HResult;
        // LogError preserves exception types/stack and deduplicates the same
        // exception as it travels from the synthesizer to the playback service.
        diagnostics.LogError("speech.failure", error, Context);
    }
}
