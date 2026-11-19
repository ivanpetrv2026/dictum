using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Dictum.Services;

/// <summary>
/// Append-only, structured diagnostic events. Deliberately has no parameter for
/// dictated words: a phase/outcome/timing record cannot leak a transcript even
/// by accident. dictum.log keeps the human-readable metrics; this file is the
/// machine-readable timeline (one JSON object per line).
/// </summary>
public static class Diagnostics
{
    private const long RotateAtBytes = 1024 * 1024;

    private static readonly object Gate = new();
    private static readonly HashSet<string> ForbiddenKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "text", "transcript", "words", "heard", "content", "chunk", "asr", "prompt", "message"
    };

    /// <summary>Test seam: null means %APPDATA%\Dictum.</summary>
    public static string? OverrideDirectory { get; set; }

    private static string DirectoryPath => OverrideDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Dictum");

    public static string FilePath => Path.Combine(DirectoryPath, "events.ndjson");

    public static bool IsForbidden(string key) => ForbiddenKeys.Contains(key);

    public static string Format(
        string phase,
        string outcome,
        long elapsedMs,
        params (string Key, object? Value)[] fields)
    {
        var payload = new Dictionary<string, object?>
        {
            ["ts"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            ["phase"] = phase,
            ["outcome"] = outcome,
            ["ms"] = elapsedMs
        };

        foreach (var (key, value) in fields)
        {
            if (IsForbidden(key))
            {
                throw new ArgumentException($"diagnostic field '{key}' could carry dictated words");
            }

            payload[key] = value;
        }

        return JsonSerializer.Serialize(payload);
    }

    /// <summary>
    /// Never throws into the dictation path. A caller that tries to log words
    /// gets a sanitized event with a dropped-fields count instead.
    /// </summary>
    public static void Write(
        string phase,
        string outcome,
        long elapsedMs,
        params (string Key, object? Value)[] fields)
    {
        string line;
        var dropped = 0;

        try
        {
            line = Format(phase, outcome, elapsedMs, fields);
        }
        catch (ArgumentException)
        {
            var clean = fields.Where(field => !IsForbidden(field.Key)).ToArray();
            dropped = fields.Length - clean.Length;
            line = Format(phase, outcome + "-sanitized", elapsedMs,
                clean.Append(("dropped_fields", dropped)).ToArray());
        }

        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > RotateAtBytes)
                {
                    File.Copy(FilePath, FilePath + ".1", true);
                    File.WriteAllText(FilePath, string.Empty);
                }

                File.AppendAllText(FilePath, line + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch
        {
            // Diagnostics must never break dictation.
        }
    }
}
