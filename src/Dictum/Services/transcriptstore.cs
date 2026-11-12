using System.IO;
using System.Text;

namespace Dictum.Services;

/// <summary>
/// Append-only archive of recognised texts: one plain-text file per day in
/// %APPDATA%\Dictum\transcripts. Local only, no rotation needed — a day of
/// dictation is a few kilobytes.
/// </summary>
public sealed class TranscriptStore
{
    public string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Dictum",
        "transcripts");

    public string Append(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        System.IO.Directory.CreateDirectory(Directory);

        var file = Path.Combine(Directory, DateTime.Now.ToString("yyyy-MM-dd") + ".txt");
        var line = $"{DateTime.Now:HH:mm:ss}  {text.Trim()}{Environment.NewLine}";

        File.AppendAllText(file, line, new UTF8Encoding(false));
        return file;
    }
}
