namespace TavernDesk.App.Presentation;

internal readonly record struct MarkdownMessageBlock(
    bool IsCode, string Text, bool EndsParagraph = false, bool ContinuesParagraph = false);

/// <summary>
/// Preserves the presenter's existing line-based syntax. Bounded text groups
/// keep appending a long reply from rebuilding one ever-growing inline collection.
/// Parsing remains a full, deterministic pass; only the WPF content is incremental.
/// </summary>
internal static class MarkdownMessageBlocks
{
    internal const int LinesPerBlock = 32;

    public static List<MarkdownMessageBlock> Parse(string source)
    {
        var result = new List<MarkdownMessageBlock>();
        if (source.Length == 0) return result;
        var lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var index = 0; index < lines.Length;)
        {
            if (lines[index].StartsWith("```", StringComparison.Ordinal))
            {
                var start = ++index;
                while (index < lines.Length && !lines[index].StartsWith("```", StringComparison.Ordinal)) index++;
                // The previous renderer ignored empty leading code lines.
                while (start < index && lines[start].Length == 0) start++;
                result.Add(new(true, string.Join('\n', lines, start, index - start)));
                if (index < lines.Length) index++;
                continue;
            }

            var paragraphStart = index;
            while (index < lines.Length && !lines[index].StartsWith("```", StringComparison.Ordinal)) index++;
            // Empty lines before the first inline did not emit a LineBreak.
            while (paragraphStart < index - 1 && lines[paragraphStart].Length == 0) paragraphStart++;
            for (var start = paragraphStart; start < index; start += LinesPerBlock)
            {
                var count = Math.Min(LinesPerBlock, index - start);
                result.Add(new(false, string.Join('\n', lines, start, count),
                    EndsParagraph: start + count == index, ContinuesParagraph: start != paragraphStart));
            }
        }
        return result;
    }
}
