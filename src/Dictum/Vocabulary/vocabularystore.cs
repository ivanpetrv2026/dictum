using System.IO;
using System.Text.Json;

namespace Dictum.Vocabulary;

public sealed class VocabularyStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Dictum",
        "vocabulary.json");

    public List<VocabularyTerm> Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var terms = JsonSerializer.Deserialize<List<VocabularyTerm>>(json);
                if (terms is { Count: > 0 })
                {
                    return terms;
                }
            }
        }
        catch (Exception)
        {
            // A broken dictionary must never prevent the app from starting.
        }

        var seeded = CreateDefaults();
        Save(seeded);
        return seeded;
    }

    public void Save(IEnumerable<VocabularyTerm> terms)
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(terms, JsonOptions));
    }

    /// <summary>
    /// Starting set for Russian dictation mixed with English IT terms. The
    /// variants are the shapes Whisper and Russian ears most often produce.
    /// </summary>
    private static List<VocabularyTerm> CreateDefaults() => new()
    {
        Term("ClickHouse", "click house", "clickhouse", "кликхаус"),
        Term("PostgreSQL", "postgres", "постгрес", "постгре", "постгресql"),
        Term("GitHub", "гитхаб", "гит хаб", "github"),
        Term("GitLab", "гитлаб", "гит лаб"),
        Term("Python", "питон", "пайтон"),
        Term("JavaScript", "джаваскрипт", "java script"),
        Term("TypeScript", "тайпскрипт", "type script"),
        Term("Kubernetes", "кубернетес", "кубер", "kubernetis"),
        Term("Docker", "докер"),
        Term("Redis", "редис"),
        Term("Kafka", "кафка"),
        Term("Grafana", "графана"),
        Term("Metabase", "метабейс", "метабэйс"),
        Term("Jira", "жира", "джира", "жир", "жиру", "джиру"),
        Term("Confluence", "конфлюенс"),
        Term("API", "апи", "эйпиай", "пиай", "pia", "эй пи ай"),
        Term("SQL", "скул", "эс кью эл", "sql", "сиквел"),
        Term("RDP", "рдп", "рдпэ"),
        Term("VPN", "впн"),
        Term("CI/CD", "си ай си ди"),
        Term("Pull Request", "пул реквест", "пулл реквест"),
        Term("commit", "коммит"),
        Term("deploy", "деплой"),
        Term("backend", "бэкенд", "бекенд"),
        Term("frontend", "фронтенд", "фронтэнд"),
        Term("webhook", "вебхук", "веб хук")
    };

    private static VocabularyTerm Term(string canonical, params string[] variants) => new()
    {
        Canonical = canonical,
        Variants = variants.ToList()
    };
}
