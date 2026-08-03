using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using TavernDesk.Core.Abstractions;

namespace TavernDesk.Infrastructure.Compatibility;

public sealed partial class SafeMacroEngine : IMacroEngine
{
    private const int MaximumPasses = 8;
    private const int MaximumExpandedLength = 200_000;

    public string Expand(
        string template,
        IReadOnlyDictionary<string, string> variables)
    {
        if (string.IsNullOrEmpty(template))
        {
            return string.Empty;
        }

        var result = template;
        for (var pass = 0; pass < MaximumPasses; pass++)
        {
            var changed = false;
            var next = MacroPattern().Replace(result, match =>
            {
                var replacement = Resolve(match.Groups["body"].Value, variables);
                if (replacement is null)
                {
                    return match.Value;
                }

                changed = true;
                return replacement;
            });
            if (next.Length > MaximumExpandedLength)
            {
                throw new InvalidOperationException(
                    $"宏展开结果超过 {MaximumExpandedLength} 个字符。");
            }

            result = next;
            if (!changed)
            {
                break;
            }
        }

        return result;
    }

    private static string? Resolve(
        string body,
        IReadOnlyDictionary<string, string> variables)
    {
        var trimmed = body.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        var parts = trimmed.Split("::", StringSplitOptions.None);
        var name = parts[0].Trim();
        if (TryGetVariable(variables, name, out var direct))
        {
            return direct;
        }

        return name.ToLowerInvariant() switch
        {
            "date" => TryGetVariable(variables, "date", out var date)
                ? date
                : DateTimeOffset.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "time" => TryGetVariable(variables, "time", out var time)
                ? time
                : DateTimeOffset.Now.ToString("HH:mm", CultureInfo.InvariantCulture),
            "datetime" => TryGetVariable(variables, "datetime", out var dateTime)
                ? dateTime
                : DateTimeOffset.Now.ToString(
                    "yyyy-MM-dd HH:mm",
                    CultureInfo.InvariantCulture),
            "random" or "pick" => Pick(parts.Skip(1), trimmed, variables),
            "roll" => Roll(parts.Skip(1).FirstOrDefault(), trimmed, variables),
            _ => null
        };
    }

    private static string Pick(
        IEnumerable<string> values,
        string expression,
        IReadOnlyDictionary<string, string> variables)
    {
        var candidates = values.Select(value => value.Trim()).ToArray();
        if (candidates.Length == 0)
        {
            return string.Empty;
        }

        return candidates[StableIndex(expression, variables, candidates.Length)];
    }

    private static string? Roll(
        string? expression,
        string macroBody,
        IReadOnlyDictionary<string, string> variables)
    {
        var match = DicePattern().Match(expression?.Trim() ?? string.Empty);
        if (!match.Success
            || !int.TryParse(match.Groups["count"].Value, out var count)
            || !int.TryParse(match.Groups["sides"].Value, out var sides)
            || count is < 1 or > 100
            || sides is < 2 or > 10000)
        {
            return null;
        }

        var modifier = int.TryParse(match.Groups["modifier"].Value, out var parsed)
            ? parsed
            : 0;
        var total = modifier;
        for (var index = 0; index < count; index++)
        {
            total += StableIndex(
                         $"{macroBody}:{index}",
                         variables,
                         sides)
                     + 1;
        }

        return total.ToString(CultureInfo.InvariantCulture);
    }

    private static int StableIndex(
        string expression,
        IReadOnlyDictionary<string, string> variables,
        int count)
    {
        TryGetVariable(variables, "__seed", out var seed);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}\0{expression}"));
        return (int)(BitConverter.ToUInt32(bytes, 0) % (uint)count);
    }

    private static bool TryGetVariable(
        IReadOnlyDictionary<string, string> variables,
        string name,
        out string value)
    {
        foreach (var pair in variables)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    [GeneratedRegex(@"\{\{(?<body>[^{}]+)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex MacroPattern();

    [GeneratedRegex(
        @"^(?<count>\d+)[dD](?<sides>\d+)(?<modifier>[+-]\d+)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex DicePattern();
}
