using System.IO;
using System.Text.Json;

namespace Dictum.Services;

public sealed class AppProfile
{
    public string Name { get; set; } = string.Empty;

    public List<string> Matches { get; set; } = new();

    /// <summary>Instruction for the AI cleanup pass (tone, formatting).</summary>
    public string Style { get; set; } = string.Empty;

    /// <summary>Short vocabulary hint for the Whisper prompt.</summary>
    public string Hint { get; set; } = string.Empty;
}

/// <summary>
/// Per-application profiles: match by process name and adjust both the Whisper
/// prompt and the AI cleanup instruction. Stored next to the vocabulary.
/// </summary>
public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Dictum",
        "profiles.json");

    public List<AppProfile> Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var profiles = JsonSerializer.Deserialize<List<AppProfile>>(File.ReadAllText(FilePath));
                if (profiles is { Count: > 0 })
                {
                    return profiles;
                }
            }
        }
        catch (Exception)
        {
            // Broken file must not stop dictation.
        }

        var defaults = CreateDefaults();
        Save(defaults);
        return defaults;
    }

    public void Save(IEnumerable<AppProfile> profiles)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(profiles, JsonOptions));
    }

    public static AppProfile? Resolve(IEnumerable<AppProfile> profiles, string application)
    {
        var name = application.ToLowerInvariant();

        foreach (var profile in profiles)
        {
            foreach (var match in profile.Matches)
            {
                if (match.Length > 0 && name.Contains(match.ToLowerInvariant()))
                {
                    return profile;
                }
            }
        }

        return null;
    }

    private static List<AppProfile> CreateDefaults() => new()
    {
        new AppProfile
        {
            Name = "Код",
            Matches = new List<string> { "code", "devenv", "rider", "pycharm", "sublime", "notepad++" },
            Style = "Технический стиль: сохраняй имена функций, переменных и термины без изменений.",
            Hint = "Программирование: функции, переменные, API, репозиторий, коммит."
        },
        new AppProfile
        {
            Name = "Чат",
            Matches = new List<string> { "telegram", "discord", "slack", "whatsapp", "teams", "viber" },
            Style = "Разговорный стиль, короткие фразы, без канцелярита.",
            Hint = "Сообщение в чате, разговорная речь."
        },
        new AppProfile
        {
            Name = "Почта",
            Matches = new List<string> { "outlook", "thunderbird", "mail" },
            Style = "Деловой стиль, полные предложения, вежливая форма.",
            Hint = "Деловое письмо."
        }
    };
}
