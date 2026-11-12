using System.Text;
using System.Text.RegularExpressions;

namespace Dictum.Services;

/// <summary>
/// Offline finishing pass used when AI cleanup is disabled or unavailable:
/// restores sentence casing, fixes spacing, turns spoken punctuation into real
/// symbols and guarantees a terminal period. Deliberately rule-based — a full
/// punctuation model would add hundreds of megabytes for a small gain.
/// </summary>
public static class LocalPolisher
{
    private static readonly (string Spoken, string Symbol)[] Commands =
    {
        ("точка с запятой", ";"),
        ("вопросительный знак", "?"),
        ("восклицательный знак", "!"),
        ("новая строка", "\n"),
        ("новый абзац", "\n\n"),
        ("запятая", ","),
        ("двоеточие", ":")
    };

    public static string Polish(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var result = " " + text.Trim() + " ";

        foreach (var (spoken, symbol) in Commands)
        {
            result = Regex.Replace(
                result,
                @"\s+" + Regex.Escape(spoken) + @"\s+",
                symbol == "\n" || symbol == "\n\n" ? symbol : symbol + " ",
                RegexOptions.IgnoreCase);
        }

        result = Regex.Replace(result, @"[ \t]+", " ");
        result = Regex.Replace(result, @"\s+([,.;:!?])", "$1");
        result = Regex.Replace(result, @"([,.;:!?])(?=[^\s\n])", "$1 ");
        result = Regex.Replace(result, @" *\n *", "\n");
        result = Regex.Replace(result, @"\n{3,}", "\n\n");
        result = result.Trim();

        var builder = new StringBuilder(result.Length);
        var capitalizeNext = true;

        foreach (var ch in result)
        {
            if (capitalizeNext && char.IsLetter(ch))
            {
                builder.Append(char.ToUpperInvariant(ch));
                capitalizeNext = false;
                continue;
            }

            builder.Append(ch);

            if (ch is '.' or '!' or '?' or '\n')
            {
                capitalizeNext = true;
            }
        }

        var polished = builder.ToString().Trim();

        if (polished.Length > 0 && !".!?…".Contains(polished[^1]))
        {
            polished += ".";
        }

        return polished;
    }
}
