using System.IO;
using System.Text;
using Dictum.Vocabulary;

namespace Dictum.Services;

/// <summary>
/// Headless check of the term pipeline: `Dictum.exe --selftest` writes the
/// corrected samples to %APPDATA%\Dictum\selftest.txt and exits.
/// </summary>
internal static class SelfTest
{
    public static void Run()
    {
        var vocabulary = new VocabularyStore().Load();
        var corrector = new TermCorrector(vocabulary);

        var samples = new[]
        {
            "мы используем click house и github а еще pia и sql отправь задачу в жир до пятницы",
            "подними кубернетес и докер проверь графану и метабейс",
            "сделай коммит и деплой через си ай си ди",
            "у меня упал постгрес и редис, смотри графану",
            "открой пул реквест и посмотри впн"
        };

        var report = new StringBuilder();
        report.AppendLine($"terms: {corrector.TermCount}");
        report.AppendLine($"prompt: {string.Join(", ", vocabulary.Select(term => term.Canonical).Take(30))}.");
        report.AppendLine();

        foreach (var sample in samples)
        {
            report.AppendLine("IN : " + sample);
            report.AppendLine("OUT: " + corrector.Correct(sample));
            report.AppendLine();
        }

        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Dictum",
            "selftest.txt");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, report.ToString());
    }
}
