using System.Text;
using System.Text.RegularExpressions;

namespace Dictum.Vocabulary;

/// <summary>
/// Repairs IT terms in a raw transcript: exact variant matches first
/// ("click house" → "ClickHouse"), then a one-edit fuzzy pass for longer Latin
/// words ("kubenetes" → "Kubernetes"). Russian text is never fuzzy-matched, so
/// ordinary words cannot be mangled.
/// </summary>
public sealed class TermCorrector
{
    private static readonly Regex TokenPattern = new(@"[\p{L}\p{N}]+(?:[-_/][\p{L}\p{N}]+)*", RegexOptions.Compiled);

    private readonly Dictionary<string, string> _exact = new(StringComparer.Ordinal);
    private readonly List<KeyValuePair<string, string>> _fuzzy = new();
    private readonly List<KeyValuePair<string, string>> _stems = new();

    public TermCorrector(IEnumerable<VocabularyTerm> terms)
    {
        foreach (var term in terms)
        {
            if (string.IsNullOrWhiteSpace(term.Canonical))
            {
                continue;
            }

            var canonical = term.Canonical.Trim();
            AddVariant(canonical, canonical);

            foreach (var variant in term.Variants)
            {
                AddVariant(variant, canonical);
            }
        }
    }

    public int TermCount => _exact.Count;

    public string Correct(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var tokens = TokenPattern.Matches(text).Cast<Match>().ToList();
        if (tokens.Count == 0)
        {
            return text;
        }

        var result = new StringBuilder(text.Length + 16);
        var position = 0;
        var index = 0;

        while (index < tokens.Count)
        {
            string? replacement = null;
            var consumed = 1;

            var maxSpan = Math.Min(3, tokens.Count - index);
            for (var span = maxSpan; span >= 1; span--)
            {
                var parts = new List<string>(span);
                for (var k = 0; k < span; k++)
                {
                    parts.Add(tokens[index + k].Value);
                }

                if (_exact.TryGetValue(Normalize(string.Join(" ", parts)), out var canonical))
                {
                    replacement = canonical;
                    consumed = span;
                    break;
                }
            }

            if (replacement is null)
            {
                if (!IsAscii(Normalize(tokens[index].Value)))
                {
                    replacement = TryStem(tokens[index].Value);
                }

                replacement ??= TryFuzzy(tokens[index].Value);
            }

            var first = tokens[index];
            var lastIndex = Math.Min(index + consumed - 1, tokens.Count - 1);
            var last = tokens[lastIndex];

            result.Append(text, position, first.Index - position);
            result.Append(replacement ?? text.Substring(first.Index, last.Index + last.Length - first.Index));
            position = last.Index + last.Length;
            index = lastIndex + 1;
        }

        result.Append(text, position, text.Length - position);
        return result.ToString();
    }

    private static readonly string[] RussianEndings =
    {
        "ами", "ах", "ой", "ей", "ам", "ям", "ы", "и", "а", "я", "у", "ю", "е", "о"
    };

    /// <summary>
    /// Handles Russian inflection: the dictionary stores "Графана", the
    /// transcript says "графану" — the stem "графан" covers both.
    /// </summary>
    private string? TryStem(string word)
    {
        var normalized = Normalize(word);

        foreach (var pair in _stems)
        {
            if (normalized.Length >= pair.Key.Length
                && normalized.StartsWith(pair.Key, StringComparison.Ordinal)
                && normalized.Length - pair.Key.Length <= 2)
            {
                return pair.Value;
            }
        }

        return null;
    }

    private string? TryFuzzy(string word)
    {
        if (word.Length < 4)
        {
            return null;
        }

        var normalized = Normalize(word);
        if (normalized.Length < 4 || !IsAscii(normalized))
        {
            return null;
        }

        foreach (var pair in _fuzzy)
        {
            if (Math.Abs(pair.Key.Length - normalized.Length) > 1)
            {
                continue;
            }

            if (Damerau(normalized, pair.Key) <= 1)
            {
                return pair.Value;
            }
        }

        return null;
    }

    private void AddVariant(string variant, string canonical)
    {
        var normalized = Normalize(variant);
        if (normalized.Length == 0)
        {
            return;
        }

        _exact[normalized] = canonical;

        if (normalized.Length >= 4 && IsAscii(normalized))
        {
            _fuzzy.Add(new KeyValuePair<string, string>(normalized, canonical));
        }

        if (!IsAscii(normalized) && normalized.Length >= 5)
        {
            foreach (var ending in RussianEndings)
            {
                if (normalized.Length - ending.Length >= 4 && normalized.EndsWith(ending, StringComparison.Ordinal))
                {
                    _stems.Add(new KeyValuePair<string, string>(normalized[..^ending.Length], canonical));
                    break;
                }
            }
        }
    }

    public static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);

        foreach (var ch in value.ToLowerInvariant())
        {
            if (!char.IsLetterOrDigit(ch))
            {
                continue;
            }

            builder.Append(ch == 'ё' ? 'е' : ch);
        }

        return builder.ToString();
    }

    private static bool IsAscii(string value) => value.All(ch => ch < 128);

    /// <summary>Optimal string alignment distance (Damerau-Levenshtein without transposition chains).</summary>
    private static int Damerau(string a, string b)
    {
        var rows = a.Length + 1;
        var cols = b.Length + 1;
        var d = new int[rows, cols];

        for (var i = 0; i < rows; i++) d[i, 0] = i;
        for (var j = 0; j < cols; j++) d[0, j] = j;

        for (var i = 1; i < rows; i++)
        {
            for (var j = 1; j < cols; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;

                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);

                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
                }
            }
        }

        return d[rows - 1, cols - 1];
    }
}
