using System.IO;

namespace Dictum.Services;

/// <summary>
/// Minimal append-only diagnostic log. No telemetry, no network — the file
/// lives next to settings.json and is meant for troubleshooting only.
/// </summary>
internal static class Log
{
    private static readonly object Gate = new();

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Dictum",
        "dictum.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never break dictation.
        }
    }
}
