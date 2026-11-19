namespace Dictum.Vocabulary;

public sealed record PromptPlan(
    string Text,
    int TermsRead,
    int TermsTotal,
    int Characters,
    int Budget,
    int TermCap,
    bool Truncated);

/// <summary>
/// Builds Whisper's vocabulary prompt within an explicit budget. The old code
/// took the first 30 terms blindly; a long user dictionary could overflow the
/// prompt slot and silently push the app-specific hint out. Terms are never cut
/// in half, and the number actually read is reported for diagnostics.
/// </summary>
public static class PromptBudget
{
    public const int DefaultMaxChars = 500;
    public const int DefaultMaxTerms = 30;

    public static PromptPlan Build(
        IEnumerable<string> terms,
        int maxChars = DefaultMaxChars,
        int maxTerms = DefaultMaxTerms)
    {
        var budget = Math.Max(16, maxChars);
        var cap = Math.Max(1, maxTerms);

        var all = terms
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .Select(term => term.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var chosen = new List<string>();
        var used = 0;

        foreach (var term in all)
        {
            if (chosen.Count >= cap)
            {
                break;
            }

            var addition = chosen.Count == 0 ? term.Length : term.Length + 2; // ", "
            if (used + addition > budget)
            {
                break;
            }

            chosen.Add(term);
            used += addition;
        }

        var text = string.Join(", ", chosen) + ".";

        return new PromptPlan(
            text,
            chosen.Count,
            all.Count,
            text.Length,
            budget,
            cap,
            chosen.Count < all.Count);
    }
}
