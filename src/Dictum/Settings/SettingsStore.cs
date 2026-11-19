using System.IO;
using Dictum.Services;

namespace Dictum.Settings;

public sealed class SettingsStore
{
    private SettingsLoadResult _last = new();

    public SettingsStore(string? dataDirectory = null)
    {
        DataDirectory = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Dictum");
    }

    public string DataDirectory { get; }

    public string FilePath => Path.Combine(DataDirectory, "settings.json");

    public IReadOnlyList<string> RepairedFields => _last.RepairedFields;

    public bool WasUnreadable => _last.UnreadableBytes is not null;

    public AppSettings Load()
    {
        _last = SettingsGuard.Load(FilePath);

        if (_last.UnreadableBytes is not null)
        {
            Log.Write($"settings: file is unreadable, using defaults; original preserved as {SettingsGuard.BackupNameFor(_last.UnreadableBytes)}");
        }
        else if (_last.RepairedFields.Count > 0)
        {
            Log.Write($"settings: invalid field(s) replaced with defaults: {string.Join(", ", _last.RepairedFields)}");
        }

        return _last.Settings;
    }

    public void Save(AppSettings settings)
    {
        SettingsGuard.Save(FilePath, settings, _last.Original, _last.UnreadableBytes);

        // Re-read so an external edit's unknown keys keep surviving the next save.
        _last = SettingsGuard.Load(FilePath);
        _last = new SettingsLoadResult
        {
            Settings = settings,
            Original = _last.Original,
            RepairedFields = _last.RepairedFields
        };
    }
}
