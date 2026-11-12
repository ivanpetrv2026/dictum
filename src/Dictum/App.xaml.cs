using System.Threading;
using System.Windows;
using Dictum.Services;
using Dictum.Settings;
using Dictum.Views;
using Dictum.Vocabulary;

namespace Dictum;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;
    private AppController? _controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--mic-diag"))
        {
            var report = new System.Text.StringBuilder();

            try
            {
                // Reporting only — the diagnostic must not change user settings.
                using (var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator())
                {
                    foreach (var device in enumerator.EnumerateAudioEndPoints(
                                 NAudio.CoreAudioApi.DataFlow.Capture,
                                 NAudio.CoreAudioApi.DeviceState.Active))
                    {
                        var volume = device.AudioEndpointVolume;
                        report.AppendLine($"endpoint: {device.FriendlyName} | vol={volume.MasterVolumeLevelScalar:P0} | mute={volume.Mute}");
                    }
                }

                foreach (var rate in new[] { 16000, 48000 })
                {
                    using var recorder = new Dictum.Audio.AudioRecorder();
                    recorder.Start(0, rate);
                    System.Threading.Thread.Sleep(3000);
                    var pcm = recorder.Stop();

                    var peak = Dictum.Audio.AudioNormalizer.Peak(pcm);
                    var stats = Dictum.Audio.AudioStats.Analyze(pcm);
                    var (start, end) = Dictum.Audio.Vad.FindSpeech(pcm);
                    report.AppendLine($"{rate} Hz: samples={pcm.Length / 2} peak={peak} rms={stats.Rms:F0} zcr={stats.Zcr:F3} flatness={stats.Flatness:F1} vad={start / (double)rate:F2}..{end / (double)rate:F2}");

                    if (Dictum.Audio.AudioStats.LooksLikeHum(stats))
                    {
                        report.AppendLine("verdict: humming input — check the plug and cable, try another jack");
                    }
                    else if (peak < 800)
                    {
                        report.AppendLine("verdict: very quiet — the mic may be muted or disconnected");
                    }
                    else if (end > start)
                    {
                        report.AppendLine("verdict: speech detected, the mic works");
                    }
                    else
                    {
                        report.AppendLine("verdict: no speech detected in the take");
                    }
                }
            }
            catch (Exception ex)
            {
                report.AppendLine("error: " + ex);
            }

            var diagPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Dictum",
                "mic-diag.txt");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(diagPath)!);
            System.IO.File.WriteAllText(diagPath, report.ToString());
            Shutdown();
            return;
        }

        if (e.Args.Contains("--benchmark"))
        {
            var report = new System.Text.StringBuilder();

            try
            {
                CudaBootstrap.Prepare();
                var current = new SettingsStore().Load();
                var vocabulary = new VocabularyStore().Load();
                var prompt = string.Join(", ", vocabulary.Select(term => term.Canonical).Take(30)) + ".";

                var transcriber = new Dictum.Speech.WhisperTranscriber(current.ModelPath, current.Language, prompt);
                var result = BenchmarkService.RunAsync(transcriber).GetAwaiter().GetResult();

                report.AppendLine(result.Summary);
                report.AppendLine();
                report.AppendLine(result.Details);
            }
            catch (Exception ex)
            {
                report.AppendLine("error: " + ex);
            }

            var benchmarkPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Dictum",
                "benchmark.txt");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(benchmarkPath)!);
            System.IO.File.WriteAllText(benchmarkPath, report.ToString());
            Shutdown();
            return;
        }

        if (e.Args.Contains("--polish-test"))
        {
            var index = Array.IndexOf(e.Args, "--polish-test");
            var sample = index + 1 < e.Args.Length
                ? e.Args[index + 1]
                : "привет как дела запятая что нового вопросительный знак новая строка все хорошо";

            var polished = Dictum.Services.LocalPolisher.Polish(sample);
            var reportPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Dictum",
                "polish-test.txt");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(reportPath)!);
            System.IO.File.WriteAllText(reportPath, "in : " + sample + Environment.NewLine + "out: " + polished);
            Shutdown();
            return;
        }

        if (e.Args.Contains("--vad-test"))
        {
            var report = new System.Text.StringBuilder();

            try
            {
                var index = Array.IndexOf(e.Args, "--vad-test");
                var file = index + 1 < e.Args.Length ? e.Args[index + 1] : string.Empty;
                var bytes = System.IO.File.ReadAllBytes(file);

                var dataIndex = -1;
                for (var i = 12; i < bytes.Length - 8; i++)
                {
                    if (bytes[i] == (byte)'d' && bytes[i + 1] == (byte)'a' && bytes[i + 2] == (byte)'t' && bytes[i + 3] == (byte)'a')
                    {
                        dataIndex = i + 8;
                        break;
                    }
                }

                if (dataIndex < 0)
                {
                    throw new InvalidOperationException("no data chunk");
                }

                var pcm = new byte[bytes.Length - dataIndex];
                Array.Copy(bytes, dataIndex, pcm, 0, pcm.Length);

                var (normalized, gain) = Dictum.Audio.AudioNormalizer.Normalize(pcm);
                var (start, end) = Dictum.Audio.Vad.FindSpeech(normalized);
                var peak = Dictum.Audio.AudioNormalizer.Peak(pcm);
                var stats = Dictum.Audio.AudioStats.Analyze(pcm);

                report.AppendLine($"total: {pcm.Length / 32000.0:F2}s, peak {peak}");
                report.AppendLine($"speech: {start / 16000.0:F2}s .. {end / 16000.0:F2}s ({(end - start) / 16000.0:F2}s)");
                report.AppendLine($"gain: {gain:F1} dB");
                report.AppendLine($"signal: rms={stats.Rms:F0} zcr={stats.Zcr:F3} flatness={stats.Flatness:F1}");
                report.AppendLine(Dictum.Audio.AudioStats.LooksLikeHum(stats)
                    ? "verdict: humming input — check the plug and cable, try another jack"
                    : "verdict: looks like speech or normal audio");
            }
            catch (Exception ex)
            {
                report.AppendLine("error: " + ex);
            }

            var reportPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Dictum",
                "vad-test.txt");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(reportPath)!);
            System.IO.File.WriteAllText(reportPath, report.ToString());
            Shutdown();
            return;
        }

        if (e.Args.Contains("--setup"))
        {
            _controller = new AppController();
            _controller.Start();
            _controller.ShowOnboarding();
            return;
        }

        if (e.Args.Contains("--indicator-test"))
        {
            var indicator = new RecordingIndicator();
            indicator.PlaceNearBottom();
            indicator.Show();

            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                indicator.Close();
                Shutdown();
            };
            timer.Start();
            return;
        }

        if (e.Args.Contains("--ai-test"))
        {
            var output = new System.Text.StringBuilder();
            try
            {
                var index = Array.IndexOf(e.Args, "--ai-test");
                var sample = index + 1 < e.Args.Length
                    ? e.Args[index + 1]
                    : "привет это тест пунктуации и очистки текста мы используем click house и github";

                var settings = new SettingsStore().Load();
                var cleanup = new AiCleanup(
                    settings.AiProvider,
                    AiCleanup.ResolveKey(settings.AiProvider) ?? string.Empty,
                    settings.AiModel,
                    settings.AiBaseUrl);

                output.AppendLine("provider: " + settings.AiProvider);
                output.AppendLine("endpoint: " + settings.AiBaseUrl);
                output.AppendLine("model: " + settings.AiModel);
                output.AppendLine("configured: " + cleanup.IsConfigured);
                output.AppendLine("in : " + sample);

                var result = cleanup.CleanAsync(sample, 20).GetAwaiter().GetResult();
                output.AppendLine("out: " + (result ?? "<null>"));

                if (result is null && settings.AiFallbackEnabled)
                {
                    var fallbackCleanup = new AiCleanup(
                        "gemini",
                        AiCleanup.ResolveKey("gemini") ?? string.Empty,
                        settings.AiFallbackModel,
                        AiCleanup.DefaultEndpoint("gemini"));

                    output.AppendLine("fallback configured: " + fallbackCleanup.IsConfigured);
                    var fallbackResult = fallbackCleanup.CleanAsync(sample, 20).GetAwaiter().GetResult();
                    output.AppendLine("fallback out: " + (fallbackResult ?? "<null>"));
                }
            }
            catch (Exception ex)
            {
                output.AppendLine("error: " + ex);
            }

            var aiPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Dictum",
                "ai-test.txt");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(aiPath)!);
            System.IO.File.WriteAllText(aiPath, output.ToString());
            Shutdown();
            return;
        }

        if (e.Args.Contains("--selftest"))
        {
            try
            {
                SelfTest.Run();
            }
            catch (Exception ex)
            {
                var errorPath = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Dictum",
                    "selftest-error.txt");
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(errorPath)!);
                System.IO.File.WriteAllText(errorPath, ex.ToString());
            }

            Shutdown();
            return;
        }

        _singleInstance = new Mutex(true, @"Local\Dictum.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        _controller = new AppController();
        _controller.Start();

        if (e.Args.Contains("--settings"))
        {
            _controller.ShowSettings();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _controller?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
