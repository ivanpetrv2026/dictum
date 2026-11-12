using System.Diagnostics;
using System.IO;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using System.Text;
using System.Text.RegularExpressions;
using Dictum.Speech;

namespace Dictum.Services;

public sealed record BenchmarkResult(int WordErrors, int Words, double Wer, TimeSpan Elapsed, string Details)
{
    public string Summary =>
        $"WER {Wer * 100:F1}%   ·   ошибок {WordErrors} из {Words}   ·   {Elapsed.TotalSeconds:F1} с";
}

/// <summary>
/// Built-in accuracy check: synthesises a fixed set of IT phrases with the
/// installed Russian voice, transcribes them through the real pipeline and
/// reports word error rate. Gives a number to compare releases against instead
/// of guessing whether a change helped.
/// </summary>
public static class BenchmarkService
{
    public static readonly string[] Phrases =
    {
        "Мы используем Python, ClickHouse и GitHub в одном проекте.",
        "Отправь задачу в Jira до пятницы и добавь комментарий.",
        "Разверни Docker на сервере и проверь логи.",
        "Обнови схему в PostgreSQL и перезапусти Redis.",
        "Проверь Kubernetes и Grafana после деплоя.",
        "Сделай коммит в ветку и открой пул реквест.",
        "Добавь API для авторизации и напиши тесты.",
        "Собери дашборд в Metabase за два дня."
    };

    public static async Task<BenchmarkResult> RunAsync(WhisperTranscriber transcriber, CancellationToken cancellationToken = default)
    {
        var totalErrors = 0;
        var totalWords = 0;
        var details = new StringBuilder();
        var stopwatch = Stopwatch.StartNew();

        foreach (var phrase in Phrases)
        {
            var pcm = await Task.Run(() => Synthesize(phrase), cancellationToken).ConfigureAwait(false);
            var text = await transcriber.TranscribeAsync(pcm, null, cancellationToken).ConfigureAwait(false);

            var errors = WordErrors(phrase, text);
            totalErrors += errors;
            totalWords += Tokenize(phrase).Length;

            details.AppendLine("• " + phrase);
            details.AppendLine("  → " + text + $"   (ошибок: {errors})");
        }

        stopwatch.Stop();

        var wer = totalWords == 0 ? 0 : (double)totalErrors / totalWords;
        return new BenchmarkResult(totalErrors, totalWords, wer, stopwatch.Elapsed, details.ToString());
    }

    private static byte[] Synthesize(string phrase)
    {
        using var stream = new MemoryStream();

        using (var synth = new SpeechSynthesizer())
        {
            var russian = synth.GetInstalledVoices()
                .FirstOrDefault(voice => voice.Enabled
                                         && voice.VoiceInfo.Culture.TwoLetterISOLanguageName == "ru");

            if (russian is not null)
            {
                synth.SelectVoice(russian.VoiceInfo.Name);
            }

            var format = new SpeechAudioFormatInfo(
                16000,
                AudioBitsPerSample.Sixteen,
                AudioChannel.Mono);

            synth.SetOutputToWaveStream(stream);
            synth.Speak(phrase);
        }

        var bytes = stream.ToArray();
        var dataIndex = -1;

        for (var i = 12; i < bytes.Length - 8; i++)
        {
            if (bytes[i] == (byte)'d' && bytes[i + 1] == (byte)'a' && bytes[i + 2] == (byte)'t' && bytes[i + 3] == (byte)'a')
            {
                dataIndex = i + 8;
                break;
            }
        }

        if (dataIndex < 0 || dataIndex >= bytes.Length)
        {
            return Array.Empty<byte>();
        }

        var pcm = new byte[bytes.Length - dataIndex];
        Array.Copy(bytes, dataIndex, pcm, 0, pcm.Length);
        return pcm;
    }

    private static string[] Tokenize(string text) =>
        Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}]+")
            .Cast<Match>()
            .Select(match => match.Value)
            .ToArray();

    private static int WordErrors(string reference, string hypothesis)
    {
        var expected = Tokenize(reference);
        var actual = Tokenize(hypothesis);

        var rows = expected.Length + 1;
        var cols = actual.Length + 1;
        var distance = new int[rows, cols];

        for (var i = 0; i < rows; i++) distance[i, 0] = i;
        for (var j = 0; j < cols; j++) distance[0, j] = j;

        for (var i = 1; i < rows; i++)
        {
            for (var j = 1; j < cols; j++)
            {
                var cost = expected[i - 1] == actual[j - 1] ? 0 : 1;

                distance[i, j] = Math.Min(
                    Math.Min(distance[i - 1, j] + 1, distance[i, j - 1] + 1),
                    distance[i - 1, j - 1] + cost);
            }
        }

        return distance[rows - 1, cols - 1];
    }
}
