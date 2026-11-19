namespace Dictum.Services;

/// <summary>
/// Decides whether a transcript can be trusted as real speech.
///
/// After the hum filter there is no reliable acoustic way to tell "quiet speech
/// under hum" from "hum only": both leave a noisy, bursty residue, and the
/// envelope statistics overlap. Whisper itself is the best speech detector we
/// have, but on a hum residue it hallucinates a small, well-known set of
/// phrases ("Продолжение следует...", "Субтитры сделал DimaTorzok", ...).
/// So the arbiter is the text: if it looks like one of those phrases, is
/// shorter than a spoken phrase, carries no letters or repeats itself, the take
/// is treated as hum and dropped. Everything else is inserted.
/// </summary>
public static class TranscriptValidator
{
    private static readonly string[] Hallucinations =
    {
        "продолжение следует",
        "продолжение в следующей",
        "субтитры",
        "редактор субтитров",
        "корректор",
        "dimatorzok",
        "dima torzok",
        "спасибо за просмотр",
        "спасибо за внимание",
        "подписывайтесь",
        "ставьте лайк",
        "the end",
        "thanks for watching",
        "please subscribe",
        "subscribe to"
    };

    // Whisper emits these as a whole answer on a hum/noise residue. They are
    // ordinary words in a real sentence, so only an exact one-word answer is
    // rejected — never a phrase that a human might actually dictate.
    private static readonly HashSet<string> ExactHallucinations = new(StringComparer.Ordinal)
    {
        "конец", "дальше", "продолжение"
    };

    /// <summary>
    /// Returns true when the text plausibly came from a human speaking.
    /// </summary>
    /// <param name="text">Raw transcript.</param>
    /// <param name="minWords">Minimum word count for the take to count as speech.</param>
    /// <param name="minLetters">Minimum number of letters in the transcript.</param>
    public static bool LooksLikeSpeech(string? text, int minWords = 1, int minLetters = 2)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();

        // Whisper marks non-speech sounds with tags: "*Ding*", "[BLANK_AUDIO]",
        // "(смех)". None of them is dictated text.
        if (trimmed.Contains('*') ||
            (trimmed.Length > 1 && (trimmed[0] == '[' || trimmed[0] == '(') &&
             (trimmed[^1] == ']' || trimmed[^1] == ')')))
        {
            return false;
        }

        var letters = 0;
        var visible = 0;
        foreach (var ch in trimmed)
        {
            if (char.IsLetter(ch))
            {
                letters++;
                visible++;
            }
            else if (!char.IsWhiteSpace(ch))
            {
                visible++;
            }
        }

        // "*Ding*", "..." and other non-verbal output carries almost no letters.
        if (letters < Math.Max(1, minLetters) || visible == 0 || letters / (double)visible < 0.5)
        {
            return false;
        }

        var normalized = Normalize(trimmed);
        if (ExactHallucinations.Contains(normalized))
        {
            return false;
        }

        foreach (var phrase in Hallucinations)
        {
            if (normalized.Contains(phrase, StringComparison.Ordinal))
            {
                return false;
            }
        }

        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < Math.Max(1, minWords))
        {
            return false;
        }

        // A single word repeated over and over is a decoder loop, not speech.
        if (words.Length >= 4 && words.Distinct(StringComparer.Ordinal).Count() == 1)
        {
            return false;
        }

        // Any three-word sequence that keeps coming back is a loop as well.
        if (words.Length >= 9)
        {
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i + 2 < words.Length; i++)
            {
                var key = words[i] + " " + words[i + 1] + " " + words[i + 2];
                seen.TryGetValue(key, out var count);
                seen[key] = count + 1;
                if (seen[key] >= 3)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static string Normalize(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        var space = false;
        foreach (var ch in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                space = false;
            }
            else if (!space && builder.Length > 0)
            {
                builder.Append(' ');
                space = true;
            }
        }

        return builder.ToString().Trim();
    }
}
