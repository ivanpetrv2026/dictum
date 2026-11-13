using System.Diagnostics;
using System.IO;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using System.Text;
using System.Text.RegularExpressions;
using Dictum.Speech;
using Dictum.Vocabulary;

namespace Dictum.Services;

public sealed record BenchmarkResult(
    int WordErrors,
    int Words,
    double Wer,
    TimeSpan Elapsed,
    string Details,
    string TtsVoice,
    string ModelName,
    DateTime StartedAt)
{
    public string Summary =>
        $"WER {Wer * 100:F1}%   ·   ошибок {WordErrors} из {Words}   ·   {Elapsed.TotalSeconds:F1} с" +
        $"   ·   {StartedAt:dd.MM HH:mm}";
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
        var startedAt = DateTime.Now;

        // The take set is fixed, so on one machine the WER repeats exactly.
        // That is expected determinism, not a canned result: every run
        // synthesises and transcribes the phrases again, and the per-phrase
        // lines below are the proof.
        var (voiceName, hasRussianVoice) = DetectRussianVoice();
        if (!hasRussianVoice)
        {
            throw new InvalidOperationException(
                "Русский TTS-голос не найден — бенчмарк будет бессмысленным. Установите русский голос в параметрах Windows (Параметры → Время и язык → Речь) и повторите.");
        }

        details.AppendLine($"Запуск: {startedAt:dd.MM.yyyy HH:mm:ss}   ·   модель: {Path.GetFileName(transcriber.ModelPath)}   ·   голос TTS: {voiceName}");
        details.AppendLine("Набор фраз фиксированный — одинаковый WER между запусками на одной машине ожидаем; различаются время и строки распознавания.");
        details.AppendLine();

        foreach (var phrase in Phrases)
        {
            var pcm = await Task.Run(() => Synthesize(phrase, voiceName), cancellationToken).ConfigureAwait(false);
            var text = await transcriber.TranscribeAsync(pcm, null, cancellationToken).ConfigureAwait(false);

            var errors = WordErrors(phrase, text);
            totalErrors += errors;
            totalWords += Tokenize(phrase).Length;

            details.AppendLine("• " + phrase);
            details.AppendLine("  → " + text + $"   (ошибок: {errors})");
        }

        stopwatch.Stop();

        var model = Path.GetFileName(transcriber.ModelPath);
        var wer = totalWords == 0 ? 0 : (double)totalErrors / totalWords;
        return new BenchmarkResult(totalErrors, totalWords, wer, stopwatch.Elapsed, details.ToString(), voiceName, model, startedAt);
    }

    /// <summary>One user phrase replayed through synthesis and recognition.</summary>
    public sealed record ContextRow(string Reference, string Recognized, string Corrected, int Errors);

    public sealed record ContextBenchmarkResult(
        int WordErrors,
        int Words,
        double Wer,
        TimeSpan Elapsed,
        string Details,
        IReadOnlyList<ContextRow> Rows,
        string TtsVoice,
        string ModelName,
        DateTime StartedAt)
    {
        public string Summary =>
            $"WER {Wer * 100:F1}%   ·   ошибок {WordErrors} из {Words}   ·   {Elapsed.TotalSeconds:F1} с   ·   {StartedAt:dd.MM HH:mm}";
    }

    /// <summary>
    /// Replays the user's own recent dictations: a TTS voice re-reads each
    /// phrase, Whisper recognises it, the vocabulary corrector applies the
    /// learned variants and the result is scored against what was actually
    /// typed. This measures the model plus the learned dictionary on the
    /// user's real content — it deliberately does not involve the microphone,
    /// so the number stays reproducible without speaking.
    /// </summary>
    public static async Task<ContextBenchmarkResult> RunContextAsync(
        WhisperTranscriber transcriber,
        IReadOnlyList<string> references,
        TermCorrector corrector,
        CancellationToken cancellationToken = default)
    {
        var totalErrors = 0;
        var totalWords = 0;
        var details = new StringBuilder();
        var stopwatch = Stopwatch.StartNew();
        var startedAt = DateTime.Now;

        var (voiceName, hasRussianVoice) = DetectRussianVoice();
        if (!hasRussianVoice)
        {
            throw new InvalidOperationException(
                "Русский TTS-голос не найден — бенчмарк будет бессмысленным. Установите русский голос в параметрах Windows (Параметры → Время и язык → Речь) и повторите.");
        }

        var rows = new List<ContextRow>();

        details.AppendLine($"Запуск: {startedAt:dd.MM.yyyy HH:mm:ss}   ·   модель: {Path.GetFileName(transcriber.ModelPath)}   ·   голос TTS: {voiceName}");
        details.AppendLine("Синтезированный голос перечитывает ваши последние фразы: это проверка модели и словаря на вашем контексте, микрофон здесь не участвует.");
        details.AppendLine();

        foreach (var reference in references)
        {
            var pcm = await Task.Run(() => Synthesize(reference, voiceName), cancellationToken).ConfigureAwait(false);
            var text = await transcriber.TranscribeAsync(pcm, null, cancellationToken).ConfigureAwait(false);
            var corrected = corrector.Correct(text);

            var errors = WordErrors(reference, corrected);
            totalErrors += errors;
            totalWords += Tokenize(reference).Length;
            rows.Add(new ContextRow(reference, text, corrected, errors));

            details.AppendLine("• сказано: " + reference);
            details.AppendLine("  распознано: " + text);
            details.AppendLine("  после словаря: " + corrected + $"   (ошибок: {errors})");
        }

        stopwatch.Stop();

        var model = Path.GetFileName(transcriber.ModelPath);
        var wer = totalWords == 0 ? 0 : (double)totalErrors / totalWords;
        return new ContextBenchmarkResult(totalErrors, totalWords, wer, stopwatch.Elapsed, details.ToString(), rows, voiceName, model, startedAt);
    }

    private static (string VoiceName, bool Found) DetectRussianVoice()
    {
        using var synth = new SpeechSynthesizer();
        var russian = synth.GetInstalledVoices()
            .FirstOrDefault(voice => voice.Enabled
                                     && voice.VoiceInfo.Culture.TwoLetterISOLanguageName == "ru");
        return russian is null ? ("нет", false) : (russian.VoiceInfo.Name, true);
    }

    private static byte[] Synthesize(string phrase, string voiceName)
    {
        using var stream = new MemoryStream();

        using (var synth = new SpeechSynthesizer())
        {
            if (voiceName != "нет")
            {
                synth.SelectVoice(voiceName);
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
