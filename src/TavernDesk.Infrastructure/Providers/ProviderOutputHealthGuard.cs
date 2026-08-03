using System.Text;

namespace TavernDesk.Infrastructure.Providers;

public sealed class ProviderOutputLoopException : InvalidOperationException
{
    public ProviderOutputLoopException()
        : base("检测到模型正文连续重复，已中止本次 API 接收。")
    {
    }
}

public sealed class ProviderOutputHealthGuard
{
    private const int MinimumPeriodCharacters = 16;
    private const int MaximumPeriodCharacters = 512;
    private const int RequiredConsecutiveRepeats = 5;
    private const int MaximumBufferedCharacters =
        MaximumPeriodCharacters * RequiredConsecutiveRepeats;
    private readonly StringBuilder _tail = new();

    public void Observe(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return;
        }

        _tail.Append(content);
        if (_tail.Length > MaximumBufferedCharacters)
        {
            _tail.Remove(0, _tail.Length - MaximumBufferedCharacters);
        }

        if (_tail.Length < MinimumPeriodCharacters * RequiredConsecutiveRepeats)
        {
            return;
        }

        var tail = _tail.ToString();
        var maximumPeriod = Math.Min(
            MaximumPeriodCharacters,
            tail.Length / RequiredConsecutiveRepeats);
        for (var period = MinimumPeriodCharacters;
             period <= maximumPeriod;
             period++)
        {
            var patternStart = tail.Length - period;
            var pattern = tail.AsSpan(patternStart, period);
            if (CountMeaningfulCharacters(pattern) < 16)
            {
                continue;
            }

            var repeated = true;
            for (var repeat = 2; repeat <= RequiredConsecutiveRepeats; repeat++)
            {
                var comparisonStart = tail.Length - (period * repeat);
                if (!tail.AsSpan(comparisonStart, period).SequenceEqual(pattern))
                {
                    repeated = false;
                    break;
                }
            }

            if (repeated)
            {
                throw new ProviderOutputLoopException();
            }
        }
    }

    private static int CountMeaningfulCharacters(ReadOnlySpan<char> value)
    {
        var count = 0;
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                count++;
            }
        }

        return count;
    }
}
