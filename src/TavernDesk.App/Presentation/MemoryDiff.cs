namespace TavernDesk.App.Presentation;

public enum MemoryDiffKind { Unchanged, Added, Removed }
public sealed record MemoryDiffLine(MemoryDiffKind Kind, string Text)
{
    public string DisplayText => (Kind == MemoryDiffKind.Added ? "+ " : Kind == MemoryDiffKind.Removed ? "− " : "  ") + Text;
}

public static class MemoryDiff
{
    public static IReadOnlyList<MemoryDiffLine> Compare(string before, string after)
    {
        static string[] Lines(string value) => value.Length == 0 ? [] : value.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var oldLines = Lines(before);
        var newLines = Lines(after);
        var result = new List<MemoryDiffLine>();
        // Bound quadratic work for long memories; retain common edges and show the changed middle as a replacement.
        if ((long)(oldLines.Length + 1) * (newLines.Length + 1) > 250_000)
        {
            var prefix = 0;
            while (prefix < Math.Min(oldLines.Length, newLines.Length) && oldLines[prefix] == newLines[prefix])
                result.Add(new(MemoryDiffKind.Unchanged, oldLines[prefix++]));
            var suffix = 0;
            while (suffix < Math.Min(oldLines.Length, newLines.Length) - prefix
                   && oldLines[^(suffix + 1)] == newLines[^(suffix + 1)]) suffix++;
            result.AddRange(oldLines.Skip(prefix).Take(oldLines.Length - prefix - suffix).Select(line => new MemoryDiffLine(MemoryDiffKind.Removed, line)));
            result.AddRange(newLines.Skip(prefix).Take(newLines.Length - prefix - suffix).Select(line => new MemoryDiffLine(MemoryDiffKind.Added, line)));
            result.AddRange(newLines.Skip(newLines.Length - suffix).Select(line => new MemoryDiffLine(MemoryDiffKind.Unchanged, line)));
            return result;
        }
        var lengths = new int[oldLines.Length + 1, newLines.Length + 1];
        for (var i = oldLines.Length - 1; i >= 0; i--)
            for (var j = newLines.Length - 1; j >= 0; j--)
                lengths[i, j] = oldLines[i] == newLines[j] ? lengths[i + 1, j + 1] + 1
                    : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
        var a = 0; var b = 0;
        while (a < oldLines.Length || b < newLines.Length)
        {
            if (a < oldLines.Length && b < newLines.Length && oldLines[a] == newLines[b])
            { result.Add(new(MemoryDiffKind.Unchanged, oldLines[a++])); b++; }
            else if (a < oldLines.Length && (b == newLines.Length || lengths[a + 1, b] >= lengths[a, b + 1]))
                result.Add(new(MemoryDiffKind.Removed, oldLines[a++]));
            else result.Add(new(MemoryDiffKind.Added, newLines[b++]));
        }
        return result;
    }
}
