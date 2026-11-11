using System.IO;
using System.Text.Json;

namespace Dictum.Services;

public sealed class RecentEntry
{
    public DateTime Time { get; set; }

    public string Text { get; set; } = string.Empty;
}

public sealed class StatsSnapshot
{
    public string Date { get; set; } = string.Empty;

    public int Today { get; set; }

    public int Total { get; set; }

    public List<RecentEntry> Recent { get; set; } = new();
}

/// <summary>
/// Local dictation counters and the last few phrases, shown on the dashboard.
/// Nothing leaves the machine.
/// </summary>
public sealed class StatsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Dictum",
        "stats.json");

    public StatsSnapshot Load()
    {
        StatsSnapshot? snapshot = null;

        try
        {
            if (File.Exists(FilePath))
            {
                snapshot = JsonSerializer.Deserialize<StatsSnapshot>(File.ReadAllText(FilePath));
            }
        }
        catch (Exception)
        {
            // Corrupted stats must not break the app.
        }

        snapshot ??= new StatsSnapshot();
        var today = DateTime.Today.ToString("yyyy-MM-dd");

        if (!string.Equals(snapshot.Date, today, StringComparison.Ordinal))
        {
            snapshot.Date = today;
            snapshot.Today = 0;
        }

        return snapshot;
    }

    public StatsSnapshot Record(string text)
    {
        var snapshot = Load();

        snapshot.Today++;
        snapshot.Total++;
        snapshot.Recent.Insert(0, new RecentEntry
        {
            Time = DateTime.Now,
            Text = text.Length > 160 ? text[..160] : text
        });

        if (snapshot.Recent.Count > 10)
        {
            snapshot.Recent.RemoveRange(10, snapshot.Recent.Count - 10);
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(snapshot, JsonOptions));
        }
        catch (Exception)
        {
            // Statistics are best effort.
        }

        return snapshot;
    }
}
