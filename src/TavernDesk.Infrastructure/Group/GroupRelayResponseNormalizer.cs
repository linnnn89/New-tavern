using System.Text.Json;

namespace TavernDesk.Infrastructure.Group;

public enum GroupRelayResponseStatus
{
    PlainText,
    UnwrappedEnvelope,
    Invalid
}

public sealed record GroupRelayResponse(
    GroupRelayResponseStatus Status,
    string Content)
{
    public bool IsValid => Status != GroupRelayResponseStatus.Invalid;
}

/// <summary>
/// Converts the small response envelope occasionally emitted by chat models
/// into displayable group-chat text. Ordinary prose is left untouched; only an
/// object with the expected speaker/content shape is unwrapped.
/// </summary>
public static class GroupRelayResponseNormalizer
{
    private const int MaximumEnvelopeCharacters = 512 * 1024;

    public static GroupRelayResponse Normalize(
        string? rawContent,
        string? expectedSpeakerName = null)
    {
        var content = StripSyntheticHistoryPrefix(rawContent, expectedSpeakerName)
            .Trim();
        if (content.Length == 0)
        {
            return Invalid();
        }

        if (LooksLikeStructuralFragment(content))
        {
            return Invalid();
        }

        var jsonCandidate = RemoveOptionalCodeFence(
            RemoveProviderRequestIdPrefix(content));
        if (jsonCandidate.Length > MaximumEnvelopeCharacters
            || !jsonCandidate.StartsWith("{", StringComparison.Ordinal))
        {
            return PlainText(DecodeEscapedLineBreaks(content));
        }

        if (!TryReadEnvelope(jsonCandidate, expectedSpeakerName, out var body))
        {
            return jsonCandidate.Contains(
                       "\"speaker\"",
                       StringComparison.OrdinalIgnoreCase)
                ? Invalid()
                : PlainText(DecodeEscapedLineBreaks(content));
        }

        return Unwrapped(DecodeEscapedLineBreaks(body));
    }

    /// <summary>
    /// Removes only an attribution prefix that the application itself has
    /// placed in group history.  The expected speaker check prevents a reply
    /// for one character from silently accepting another character's label.
    /// This is also used while rebuilding context so an old, already-saved
    /// label cannot become another repeated example for the model.
    /// </summary>
    public static string StripSyntheticHistoryPrefix(
        string? rawContent,
        string? expectedSpeakerName)
    {
        var content = rawContent ?? string.Empty;
        if (string.IsNullOrWhiteSpace(expectedSpeakerName))
        {
            return content;
        }

        var speaker = expectedSpeakerName.Trim();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var prefix = FindSyntheticPrefix(content, speaker);
            if (prefix is null)
            {
                break;
            }

            content = content[prefix.Length..].TrimStart();
        }

        return content;
    }

    public static string ForRelayPlanning(string? rawContent)
    {
        var normalized = Normalize(rawContent);
        return normalized.IsValid ? normalized.Content : string.Empty;
    }

    private static bool TryReadEnvelope(
        string json,
        string? expectedSpeakerName,
        out string body)
    {
        body = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                MaxDepth = 16,
                AllowTrailingCommas = false
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("speaker", out var speaker)
                || speaker.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var kind = ReadString(speaker, "kind");
            var name = ReadString(speaker, "name");
            if (!string.Equals(
                    kind,
                    "character",
                    StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(name)
                || (!string.IsNullOrWhiteSpace(expectedSpeakerName)
                    && !string.Equals(
                        name.Trim(),
                        expectedSpeakerName.Trim(),
                        StringComparison.Ordinal)))
            {
                return false;
            }

            body = content.GetString() ?? string.Empty;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string RemoveOptionalCodeFence(string content)
    {
        if (!content.StartsWith("```", StringComparison.Ordinal)
            || !content.EndsWith("```", StringComparison.Ordinal)
            || content.Length < 6)
        {
            return content;
        }

        var firstLineEnd = content.IndexOf('\n');
        if (firstLineEnd < 0)
        {
            return content;
        }

        var language = content[3..firstLineEnd].Trim();
        if (language.Length > 0
            && !string.Equals(language, "json", StringComparison.OrdinalIgnoreCase))
        {
            return content;
        }

        return content[(firstLineEnd + 1)..^3].Trim();
    }

    /// <summary>
    /// Some provider adapters prepend the request id directly to a JSON
    /// envelope (for example: <c>guid{"speaker":...}</c>).  Keep the
    /// original text for ordinary prose, but expose the envelope to the
    /// speaker validator when this unambiguous prefix is present.
    /// </summary>
    private static string RemoveProviderRequestIdPrefix(string content)
    {
        const int GuidTextLength = 36;
        if (content.Length <= GuidTextLength
            || !Guid.TryParseExact(content[..GuidTextLength], "D", out _))
        {
            return content;
        }

        var remainder = content[GuidTextLength..];
        var openingBrace = remainder.IndexOf('{');
        if (openingBrace < 0
            || !string.IsNullOrWhiteSpace(remainder[..openingBrace]))
        {
            return content;
        }

        return remainder[openingBrace..];
    }

    private static bool LooksLikeStructuralFragment(string content)
    {
        if (content.Length > 32)
        {
            return false;
        }

        return content.All(character =>
            character is '{' or '}' or '[' or ']' or ':' or ',' or '"'
                or '\'' or '\r' or '\n' or '\t' or ' ');
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static GroupRelayResponse PlainText(string content) =>
        string.IsNullOrWhiteSpace(content)
            ? Invalid()
            : new GroupRelayResponse(GroupRelayResponseStatus.PlainText, content);

    private static GroupRelayResponse Unwrapped(string content) =>
        string.IsNullOrWhiteSpace(content)
            ? Invalid()
            : new GroupRelayResponse(
                GroupRelayResponseStatus.UnwrappedEnvelope,
                content.Trim());

    /// <summary>
    /// Converts line-break escape sequences that survived as ordinary response
    /// text. The JSON envelope is parsed before this method runs, so standard
    /// JSON escapes are already real line breaks; this only repairs plain text
    /// or double-escaped envelope content. Paired backslashes remain literal.
    /// </summary>
    private static string DecodeEscapedLineBreaks(string value)
    {
        if (value.IndexOf('\\') < 0)
        {
            return value;
        }

        var builder = new System.Text.StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '\\' || index + 1 >= value.Length)
            {
                builder.Append(value[index]);
                continue;
            }

            var next = value[index + 1];
            if (next == '\\')
            {
                builder.Append("\\\\");
                index++;
                continue;
            }

            if (next == 'r')
            {
                if (index + 3 < value.Length
                    && value[index + 2] == '\\'
                    && value[index + 3] == 'n')
                {
                    builder.Append("\r\n");
                    index += 3;
                }
                else
                {
                    builder.Append('\r');
                    index++;
                }

                continue;
            }

            if (next == 'n')
            {
                builder.Append('\n');
                index++;
                continue;
            }

            builder.Append('\\');
        }

        return builder.ToString();
    }

    private static GroupRelayResponse Invalid() =>
        new(GroupRelayResponseStatus.Invalid, string.Empty);

    private static string? FindSyntheticPrefix(string content, string speaker)
    {
        var legacy = $"【群聊历史发言｜角色：{speaker}】";
        var compact = $"（历史发言者：{speaker}）";
        var plain = $"{speaker}：";
        var westernPlain = $"{speaker}:";
        foreach (var prefix in new[] { legacy, compact, plain, westernPlain })
        {
            if (content.StartsWith(prefix, StringComparison.Ordinal))
            {
                return prefix;
            }
        }

        return null;
    }
}
