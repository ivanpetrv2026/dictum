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

                var winmm = new List<string>();
                for (var i = 0; i < NAudio.Wave.WaveInEvent.DeviceCount; i++)
                {
                    winmm.Add(NAudio.Wave.WaveInEvent.GetCapabilities(i).ProductName);
                }

                var micSettings = new SettingsStore().Load();
                var resolution = Dictum.Audio.DeviceResolver.Resolve(
                    winmm, micSettings.AudioDeviceName, micSettings.AudioDevice);
                report.AppendLine($"winmm: {winmm.Count} device(s): {string.Join(" | ", winmm)}");
                report.AppendLine($"winmm resolution: index={resolution.Index} name='{resolution.Name}' reason={resolution.Reason} ({resolution.Detail})");

                foreach (var rate in new[] { 16000, 48000 })
                {
                    using var recorder = new Dictum.Audio.AudioRecorder();
                    recorder.Start(Math.Max(0, resolution.Index), rate);
                    System.Threading.Thread.Sleep(3000);
                    var pcm = recorder.Stop();

                    var peak = Dictum.Audio.AudioNormalizer.Peak(pcm);
                    var stats = Dictum.Audio.AudioStats.Analyze(pcm);
                    var (start, end) = Dictum.Audio.Vad.FindSpeech(pcm);
                    report.AppendLine($"{rate} Hz: samples={pcm.Length / 2} peak={peak} rms={stats.Rms:F0} zcr={stats.Zcr:F3} flatness={stats.Flatness:F1} bands={stats.LowBand:F0}/{stats.HighBand:F0} vad={start / (double)rate:F2}..{end / (double)rate:F2}");

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
                var prompt = Dictum.Vocabulary.PromptBudget.Build(vocabulary.Select(term => term.Canonical)).Text;

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

        if (e.Args.Contains("--context-benchmark"))
        {
            var report = new System.Text.StringBuilder();

            try
            {
                CudaBootstrap.Prepare();
                var current = new SettingsStore().Load();
                var vocabulary = new VocabularyStore().Load();
                var corrector = new TermCorrector(vocabulary);
                var prompt = Dictum.Vocabulary.PromptBudget.Build(vocabulary.Select(term => term.Canonical)).Text;

                var transcriber = new Dictum.Speech.WhisperTranscriber(current.ModelPath, current.Language, prompt);
                var stats = new Dictum.Services.StatsStore().Load();
                var dataDirectory = new SettingsStore().DataDirectory;
                var (chunk, _) = Dictum.Services.ContextChunkSelector.SelectNext(dataDirectory, stats.Recent.Select(entry => entry.Text));

                if (string.IsNullOrWhiteSpace(chunk))
                {
                    throw new InvalidOperationException("Недостаточно фраз в истории — продиктуйте сначала пару фраз.");
                }

                var result = Dictum.Services.BenchmarkService.RunContextAsync(transcriber, new[] { chunk }, corrector).GetAwaiter().GetResult();
                report.AppendLine(result.Summary);
                report.AppendLine();
                report.AppendLine(result.Details);
            }
            catch (Exception ex)
            {
                report.AppendLine("error: " + ex);
            }

            var contextPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Dictum",
                "context-benchmark.txt");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(contextPath)!);
            System.IO.File.WriteAllText(contextPath, report.ToString());
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

        if (e.Args.Contains("--fixture-test"))
        {
            // Drives the real dictation pipeline from a WAV file instead of the
            // microphone. Nothing is pasted and no network call is made.
            var index = Array.IndexOf(e.Args, "--fixture-test");
            var fixturePath = index + 1 < e.Args.Length
                ? e.Args[index + 1]
                : Environment.GetEnvironmentVariable("DICTUM_AUDIO_FIXTURE") ?? string.Empty;

            var report = new System.Text.StringBuilder();
            var tracePath = Environment.GetEnvironmentVariable("DICTUM_FIXTURE_TRACE");
            Action<string> trace = step =>
            {
                if (!string.IsNullOrEmpty(tracePath))
                {
                    System.IO.File.AppendAllText(tracePath,
                        $"{DateTime.Now:HH:mm:ss.fff} {step}{Environment.NewLine}");
                }
            };
            trace("start");
            try
            {
                var (source, error) = Dictum.Audio.FixtureSource.TryLoad(fixturePath);
                if (source is null)
                {
                    throw new InvalidOperationException(error ?? "fixture not loaded");
                }

                var pcm = source.ToArray();
                var (normalized, gain) = Dictum.Audio.AudioNormalizer.Normalize(pcm);
                if (gain > 0.1)
                {
                    pcm = normalized;
                }

                var stats = Dictum.Audio.AudioStats.Analyze(pcm);
                report.AppendLine($"fixture: {source.Path} | {source.DurationMs} ms | {source.ChunkCount} chunks");
                report.AppendLine($"audio: peak={Dictum.Audio.AudioNormalizer.Peak(pcm)} rms={stats.Rms:F0} zcr={stats.Zcr:F3} flatness={stats.Flatness:F1} bands={stats.LowBand:F0}/{stats.HighBand:F0} gain={gain:F1} dB");
                Dictum.Services.Diagnostics.Write("fixture", "loaded", 0, ("duration_ms", source.DurationMs));

                var humRescued = false;
                if (Dictum.Audio.AudioStats.LooksLikeHum(stats))
                {
                    var filtered = Dictum.Audio.MainsHumFilter.Remove(pcm);
                    var residual = Dictum.Audio.AudioStats.Analyze(filtered);
                    var share = Dictum.Audio.AudioStats.ResidualShare(stats, residual);
                    report.AppendLine($"hum guard: residual={share:P1}");

                    if (!Dictum.Audio.AudioStats.HasRecoverableSpeech(
                            stats, residual, Dictum.Audio.AudioNormalizer.Peak(filtered)))
                    {
                        throw new InvalidOperationException("hum guard rejected the fixture: no recoverable speech");
                    }

                    pcm = filtered;
                    humRescued = true;
                    report.AppendLine("hum guard: speech recovered from hum-dominated take");
                }

                trace("audio-prepared");
                var (start, end) = Dictum.Audio.Vad.FindSpeech(pcm);
                report.AppendLine($"vad: {start / 16000.0:F2}..{end / 16000.0:F2} s");

                // Match AppController: a speechless clip must not reach Whisper,
                // but measurable energy still gets a chance and the transcript
                // validator decides.
                if (end <= start)
                {
                    if (Dictum.Audio.AudioNormalizer.Peak(pcm) < 800)
                    {
                        throw new InvalidOperationException(
                            "no speech detected in the fixture; sending it to Whisper would hallucinate");
                    }

                    report.AppendLine("vad: detector found nothing, sending the whole take");
                    start = 0;
                    end = pcm.Length / 2;
                }
                if (end > start && end <= pcm.Length / 2)
                {
                    var trimmed = new byte[(end - start) * 2];
                    Array.Copy(pcm, start * 2, trimmed, 0, trimmed.Length);
                    pcm = trimmed;
                }

                trace("vad-done");
                CudaBootstrap.Prepare();
                trace("cuda-prepared");
                var fixtureSettings = new SettingsStore().Load();
                trace("settings-loaded");
                var fixtureVocabulary = new VocabularyStore().Load();
                var fixturePrompt = Dictum.Vocabulary.PromptBudget
                    .Build(fixtureVocabulary.Select(term => term.Canonical)).Text;
                trace("prompt-built");

                // Same construction as the working --benchmark path: no forced
                // runtime order and no explicit factory disposal (disposing the
                // native factory from the startup thread can block shutdown).
                var fixtureTranscriber = new Dictum.Speech.WhisperTranscriber(
                    fixtureSettings.ModelPath, fixtureSettings.Language, fixturePrompt);
                trace("transcriber-created");
                // BenchmarkService reaches Whisper from a thread-pool continuation.
                // Calling the async enumerable synchronously on the UI thread and
                // blocking here deadlocks on WPF's SynchronizationContext.
                var text = Task.Run(
                    () => fixtureTranscriber.TranscribeAsync(pcm).GetAwaiter().GetResult())
                    .GetAwaiter().GetResult();
                trace("transcribed");

                report.AppendLine($"asr: {text.Length} chars");
                report.AppendLine("text: " + text);
                report.AppendLine("polished: " + Dictum.Services.LocalPolisher.Polish(text));
                var passed = !string.IsNullOrWhiteSpace(text);
                if (passed && !Dictum.Services.TranscriptValidator.LooksLikeSpeech(text, out var rejectReason))
                {
                    passed = false;
                    report.AppendLine("validator: transcript rejected (reason=" + rejectReason + ")");
                }

                report.AppendLine(passed ? "VERDICT: PASS" : "VERDICT: FAIL (transcript not usable)");
                if (!passed)
                {
                    Environment.ExitCode = 1;
                }
                Dictum.Services.Diagnostics.Write("fixture", passed ? "ok" : "rejected", 0,
                    ("chars", text.Length), ("hum_rescued", humRescued));
            }
            catch (Exception ex)
            {
                report.AppendLine("VERDICT: FAIL");
                report.AppendLine("error: " + ex);
                Environment.ExitCode = 1;
            }

            var fixtureReportPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Dictum", "fixture-test.txt");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fixtureReportPath)!);
            System.IO.File.WriteAllText(fixtureReportPath, report.ToString());
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
                report.AppendLine($"signal: rms={stats.Rms:F0} zcr={stats.Zcr:F3} flatness={stats.Flatness:F1} bands={stats.LowBand:F0}/{stats.HighBand:F0}");
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
