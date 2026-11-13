using System.IO;
using System.Text.Json;
using Dictum.Vocabulary;

namespace Dictum.Services;

/// <summary>
/// Rolling cursor over the user's dictation backlog: every run covers at
/// most Budget characters of the joined stream, and repeat runs continue
/// from the first not-yet-checked position. New dictations are appended to
/// the end of the pool, so the cursor never shifts under existing progress;
/// when the stream is exhausted the cursor wraps and the next pass re-checks
/// everything — including phrases the learned dictionary now fixes better.
/// </summary>
public static class ContextChunkSelector
{
    public const int Budget = 60;

    public sealed class State
    {
        public List<string> Pool { get; set; } = new();
        public int Offset { get; set; }
    }

    public static string StatePath(string dataDirectory) =>
        Path.Combine(dataDirectory, "context-benchmark.json");

    public static State Load(string dataDirectory)
    {
        var path = StatePath(dataDirectory);
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<State>(File.ReadAllText(path)) ?? new State();
            }
        }
        catch
        {
            // corrupt state must never block the benchmark: fall through to a fresh one
        }

        return new State();
    }

    public static void Save(string dataDirectory, State state)
    {
        var path = StatePath(dataDirectory);
        Directory.CreateDirectory(dataDirectory);
        File.WriteAllText(path, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Selects the next chunk (max 60 characters, cut at a word boundary)
    /// and advances the cursor. Returns the chunk plus the saved state.
    /// </summary>
    public static (string Chunk, State Current) SelectNext(string dataDirectory, IEnumerable<string> recentTexts)
    {
        var state = Load(dataDirectory);

        foreach (var text in recentTexts)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var normalized = TermCorrector.Normalize(text);
            if (state.Pool.Any(existing => TermCorrector.Normalize(existing) == normalized))
            {
                continue;
            }

            state.Pool.Add(text);
        }

        state.Pool.RemoveAll(item => string.IsNullOrWhiteSpace(item));
        var joined = string.Join(' ', state.Pool);
        if (joined.Length == 0)
        {
            Save(dataDirectory, state);
            return (string.Empty, state);
        }

        if (state.Offset >= joined.Length)
        {
            state.Offset = 0;
        }

        var budget = Math.Min(Budget, joined.Length - state.Offset);
        var slice = joined.Substring(state.Offset, budget);

        // do not end a chunk in the middle of a word unless the word itself
        // is longer than the whole budget
        if (state.Offset + slice.Length < joined.Length)
        {
            var lastSpace = slice.LastIndexOf(' ');
            if (lastSpace > 10)
            {
                slice = slice.Substring(0, lastSpace);
            }
        }

        slice = slice.Trim();
        state.Offset += slice.Length + 1;
        if (state.Offset >= joined.Length)
        {
            state.Offset = 0;
        }

        Save(dataDirectory, state);
        return (slice, state);
    }
}
