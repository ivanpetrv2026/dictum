using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using Dictum.Audio;
using Dictum.Input;
using Dictum.Settings;
using Dictum.Speech;
using Dictum.Tray;
using Dictum.Views;
using Dictum.Vocabulary;
using Whisper.net.LibraryLoader;

namespace Dictum.Services;

public sealed class AppController : IDisposable
{
    private readonly SettingsStore _store = new();
    private readonly AudioRecorder _recorder = new();
    private readonly TextInjector _injector = new();
    private HotkeyPoller _hotkey;
    private readonly TrayIcon _tray = new();
    private readonly VocabularyStore _vocabularyStore = new();
    private readonly StatsStore _statsStore = new();
    private readonly TranscriptStore _transcripts = new();
    private readonly ProfileStore _profileStore = new();
    private readonly PasteLastStore _pasteLast = new();
    private readonly Stopwatch _takeWatch = new();

    private List<AppProfile> _profiles;

    private List<VocabularyTerm> _vocabulary;
    private TermCorrector _corrector;
    private string _prompt;
    private readonly Dictionary<string, AiCleanup> _cleanups = new();
    private AppSettings _settings;
    private WhisperTranscriber? _transcriber;
    private RecordingIndicator? _indicator;
    private string _targetApp = string.Empty;
    private nint _targetWindow;
    private DateTime _recordedAt;
    private bool _busy;

    public AppController()
    {
        _settings = _store.Load();
        _profiles = _profileStore.Load();
        _vocabulary = _vocabularyStore.Load();
        _corrector = new TermCorrector(_vocabulary);
        _prompt = BuildPrompt();

        _hotkey = CreatePoller();
        _tray.SettingsRequested += (_, _) => ShowSettings();
        _tray.SetupRequested += (_, _) => ShowOnboarding();
        _tray.AboutRequested += (_, _) => ShowAbout();
        _tray.PasteLastRequested += (_, _) => PasteLast();
        _tray.ExitRequested += (_, _) => Application.Current.Shutdown();
    }

    public void Start()
    {
        CudaBootstrap.Prepare();
        Log.Write($"started, model='{_settings.ModelPath}', lang={_settings.Language}, terms={_corrector.TermCount}");
        _tray.ShowInfo("Dictum запущен", "Удерживай Ctrl+Win и говори.");

        if (string.IsNullOrWhiteSpace(_settings.ModelPath) || !File.Exists(_settings.ModelPath))
        {
            Log.Write("model missing, opening onboarding wizard");
            ShowOnboarding();
        }
    }

    private void OnPressed(object? sender, EventArgs e)
    {
        if (_busy)
        {
            return;
        }

        _targetWindow = _injector.CaptureTarget();
        _targetApp = WindowInfo.ProcessName(_targetWindow);
        _recordedAt = DateTime.UtcNow;
        _takeWatch.Restart();

        try
        {
            _recorder.Start(_settings.AudioDeviceName, _settings.AudioDevice);
            ShowIndicator();
        }
        catch (Exception ex)
        {
            Log.Write("record start failed: " + ex.Message);
            Diagnostics.Write("capture", "start-failed", _takeWatch.ElapsedMilliseconds,
                ("reason_code", "start_failed"));
            HideIndicator();
            return;
        }

        Diagnostics.Write("capture", "started", 0,
            ("device", _recorder.DeviceName),
            ("fixture", _recorder.IsFixture));
        Log.Write($"press, target=0x{_targetWindow.ToInt64():X}");
    }

    private DateTime _lastHumNoticeUtc = DateTime.MinValue;

    /// <summary>
    /// A silently skipped take looks like the app doing nothing, so surface
    /// the broken input once in a while instead of only in the log.
    /// </summary>
    private void NotifyHummingInput(string details)
    {
        var now = DateTime.UtcNow;
        if (now - _lastHumNoticeUtc < TimeSpan.FromMinutes(10))
        {
            return;
        }

        _lastHumNoticeUtc = now;
        _tray.ShowError(
            "Dictum: микрофон не подаёт речь",
            $"Устройство {_recorder.DeviceName}. {details} " +
            "Проверьте штекер и кабель, уберите усиление микрофона в панели Realtek, " +
            "попробуйте другой разъём. Метрики — %APPDATA%\\Dictum\\dictum.log");
    }

    private async void OnReleased(object? sender, EventArgs e)
    {
        var pcm = _recorder.Stop();
        HideIndicator();
        var elapsed = (DateTime.UtcNow - _recordedAt).TotalMilliseconds;
        Log.Write($"release, bytes={pcm.Length}, duration={elapsed:F0}ms");

        if (_recorder.LastTakeStalled || _recorder.LastTakeDigitallySilent)
        {
            Log.Write("audio: input stalled or delivered digital silence; the device reopens on the next press");
            Diagnostics.Write("capture", "input-unhealthy", _takeWatch.ElapsedMilliseconds,
                ("stalled", _recorder.LastTakeStalled),
                ("digital_silence", _recorder.LastTakeDigitallySilent));
        }

        Diagnostics.Write("capture", "released", _takeWatch.ElapsedMilliseconds,
            ("bytes", pcm.Length), ("duration_ms", (long)elapsed));

        if (Environment.GetEnvironmentVariable("DICTUM_DUMP_PCM") == "1" && pcm.Length > 0)
        {
            try
            {
                var dumpPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Dictum",
                    "last.pcm");
                File.WriteAllBytes(dumpPath, pcm);
                Log.Write("dump: " + dumpPath);
            }
            catch (Exception dumpError)
            {
                Log.Write("dump failed: " + dumpError.Message);
            }
        }

        if (pcm.Length == 0)
        {
            Diagnostics.Write("capture", "empty", _takeWatch.ElapsedMilliseconds);
            return;
        }

        var (normalized, gainDb) = AudioNormalizer.Normalize(pcm);
        if (gainDb > 0.1)
        {
            pcm = normalized;
        }

        var peak = AudioNormalizer.Peak(pcm);
        var stats = AudioStats.Analyze(pcm);
        Log.Write($"audio: peak={peak} ({peak / 32768.0:P0}), rms={stats.Rms:F0}, zcr={stats.Zcr:F3}, flatness={stats.Flatness:F1}, bands={stats.LowBand:F0}/{stats.HighBand:F0}, gain {gainDb:F1} dB");

        // A floating jack hums at mains frequency: loud, almost no zero
        // crossings, flat envelope. Whisper turns that into hallucinations.
        // A working but badly grounded mic can carry real speech *under* the
        // hum, so remove the stationary 50 Hz component and re-evaluate the
        // residual instead of discarding the take outright.
        var humRescued = false;
        // Zero crossings of the residual are a cheap indication of how much
        // speech survived: a hum-only residue stays at ~0.02, a take with a
        // voice at ~0.03-0.07. It is only used to decide whether a very short
        // (one-word) transcript can be trusted, never to drop speech outright.
        var humResidualZcr = 0.0;
        if (AudioStats.LooksLikeHum(stats))
        {
            var filtered = MainsHumFilter.Remove(pcm);
            var residual = AudioStats.Analyze(filtered);
            humResidualZcr = residual.Zcr;
            var share = AudioStats.ResidualShare(stats, residual);
            var residualPeak = AudioNormalizer.Peak(filtered);
            Log.Write($"audio: hum filter residual rms={residual.Rms:F0} ({share:P1} of original), zcr={residual.Zcr:F3}, flatness={residual.Flatness:F1}, bands={residual.LowBand:F0}/{residual.HighBand:F0}, peak={residualPeak}");

            if (!AudioStats.HasRecoverableSpeech(stats, residual, residualPeak))
            {
                Log.Write($"audio: looks like a disconnected/humming input (rms={stats.Rms:F0}, zcr={stats.Zcr:F3}, flatness={stats.Flatness:F1}), no recoverable speech, skipped");
                Diagnostics.Write("capture", "hum-rejected", _takeWatch.ElapsedMilliseconds,
                    ("rms", (long)stats.Rms), ("zcr", stats.Zcr),
                    ("residual_share", share), ("high_band", (long)stats.HighBand));
                NotifyHummingInput($"Записан только гул 50 Гц: после фильтрации осталось {share:P1} сигнала без признаков речи.");
                return;
            }

            var (cleaned, cleanedGain) = AudioNormalizer.Normalize(filtered);
            if (cleanedGain > 0.1)
            {
                pcm = cleaned;
                gainDb = cleanedGain;
            }

            peak = AudioNormalizer.Peak(pcm);
            humRescued = true;
            Log.Write($"audio: speech recovered from hum-dominated take (residual {share:P1}, gain {gainDb:F1} dB)");
        }

        var (speechStart, speechEnd) = Vad.FindSpeech(pcm);
        var speechSamples = speechEnd - speechStart;

        if (speechSamples < 16_000 * 0.25)
        {
            // Very quiet recordings can defeat the detector; if there is still
            // measurable energy, let the model try instead of dropping the take.
            // A hum-rescued take goes the same way: the transcript validator,
            // not the detector, decides whether it was speech.
            if (peak < 800)
            {
                Log.Write($"vad: no speech in {pcm.Length / 32000.0:F1}s (peak {peak}), skipped");
                Diagnostics.Write("capture", "no-speech", _takeWatch.ElapsedMilliseconds,
                    ("peak", (long)peak), ("hum_rescued", humRescued));
                return;
            }

            Log.Write($"vad: detector found nothing, but peak={peak} — sending the whole take");
            speechStart = 0;
            speechEnd = pcm.Length / 2;
            speechSamples = speechEnd;
        }

        if (speechStart > 0 || speechEnd < pcm.Length / 2)
        {
            var trimmed = new byte[speechSamples * 2];
            Array.Copy(pcm, speechStart * 2, trimmed, 0, trimmed.Length);
            pcm = trimmed;
        }

        Log.Write($"vad: speech {speechSamples / 16000.0:F1}s, gain {gainDb:F1} dB, app='{_targetApp}'");
        Diagnostics.Write("capture", "speech", _takeWatch.ElapsedMilliseconds,
            ("speech_ms", (long)(speechSamples / 16.0)), ("gain_db", gainDb), ("hum_rescued", humRescued));

        _busy = true;
        try
        {
            var text = await TranscribeAsync(pcm);
            // Diagnostic logs never hold dictated words — only lengths and phases.
            Log.Write($"asr: {text.Length} chars");

            // Whisper answers a hum residue — and a wordless tone — with a
            // handful of stock phrases. Those must never be inserted, so the
            // transcript itself decides whether the take was speech. A
            // hum-dominated take has to clear a slightly higher bar because its
            // residue is noisier.
            // A one-word phrase is perfectly normal dictation. The strict bar
            // applies only when the residue still looks like hum (zcr < 0.03),
            // where a single word is far more likely to be a decoder artifact.
            var residueLooksLikeSpeech = humResidualZcr >= 0.03;
            var minWords = humRescued && !residueLooksLikeSpeech ? 2 : 1;
            var minLetters = humRescued && !residueLooksLikeSpeech ? 4 : 2;
            if (!TranscriptValidator.LooksLikeSpeech(text, minWords, minLetters))
            {
                Log.Write($"asr: transcript rejected ({(string.IsNullOrWhiteSpace(text) ? 0 : text.Length)} chars, {Words(text)} words, hum_rescued={humRescued}, residual_zcr={humResidualZcr:F3})");
                Diagnostics.Write("asr", "hallucination", _takeWatch.ElapsedMilliseconds,
                    ("chars", text.Length), ("word_count", Words(text)), ("hum_rescued", humRescued));
                if (humRescued)
                {
                    NotifyHummingInput("Записан только гул 50 Гц: в остатке не нашлось речи. Проверьте штекер и уровень микрофона.");
                }

                return;
            }

            Diagnostics.Write("asr", "ok", _takeWatch.ElapsedMilliseconds, ("chars", text.Length));

            var corrected = _corrector.Correct(text);
            if (!string.Equals(corrected, text, StringComparison.Ordinal))
            {
                Log.Write($"corrector: changed {text.Length} -> {corrected.Length} chars");
            }

            var final = await ApplyAiAsync(corrected);
            Diagnostics.Write("processing", "done", _takeWatch.ElapsedMilliseconds,
                ("chars", final.Length));

            var focused = _injector.Focus(_targetWindow);
            await Task.Delay(120);
            var pasted = _injector.TypeText(final + " ");
            _pasteLast.Record(final, pasted);
            Diagnostics.Write("paste", pasted ? "ok" : "blocked", _takeWatch.ElapsedMilliseconds,
                ("focused", focused), ("chars", final.Length));

            if (!pasted)
            {
                Log.Write("paste: SendInput was blocked; text kept for Paste Last");
                _tray.ShowError(
                    "Dictum: текст не вставлен",
                    "Окно отклонило ввод. Фраза сохранена — используйте пункт трея «Вставить последнюю фразу».");
            }

            _statsStore.Record(final);

            if (_settings.SaveTranscripts)
            {
                Log.Write("transcript: " + _transcripts.Append(final));
            }

            LearnFromEdits(final);
        }
        catch (Exception ex)
        {
            Log.Write("error: " + ex);
            _tray.ShowError("Dictum: ошибка", ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task<string> TranscribeAsync(byte[] pcm)
    {
        _transcriber ??= CreateTranscriber();
        return await _transcriber.TranscribeAsync(pcm, BuildWhisperPrompt());
    }

    /// <summary>
    /// Dictionary terms plus a short hint about the app being dictated into.
    /// Whisper uses the prompt as a style and vocabulary prior.
    /// </summary>
    private string BuildWhisperPrompt()
    {
        var profile = ProfileStore.Resolve(_profiles, _targetApp);
        var hint = profile?.Hint ?? string.Empty;
        return string.IsNullOrWhiteSpace(hint) ? _prompt : _prompt + " " + hint;
    }

    private string BuildAiContext()
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(_targetApp))
        {
            parts.Add($"приложение «{_targetApp}»");
        }

        var profile = ProfileStore.Resolve(_profiles, _targetApp);
        if (profile is not null && !string.IsNullOrWhiteSpace(profile.Style))
        {
            parts.Add(profile.Style);
        }

        return string.Join(". ", parts);
    }

    private static string ContextHint(string app)
    {
        var name = app.ToLowerInvariant();

        if (name.Contains("code") || name.Contains("devenv") || name.Contains("rider")
            || name.Contains("pycharm") || name.Contains("sublime") || name.Contains("notepad++"))
        {
            return "Программирование: функции, переменные, API, репозиторий, коммит.";
        }

        if (name.Contains("telegram") || name.Contains("discord") || name.Contains("slack")
            || name.Contains("whatsapp") || name.Contains("teams") || name.Contains("viber"))
        {
            return "Сообщение в чате, разговорная речь.";
        }

        if (name.Contains("outlook") || name.Contains("thunderbird") || name.Contains("mail"))
        {
            return "Деловое письмо.";
        }

        return string.Empty;
    }

    private WhisperTranscriber CreateTranscriber()
    {
        if (string.IsNullOrWhiteSpace(_settings.ModelPath) || !File.Exists(_settings.ModelPath))
        {
            throw new InvalidOperationException("Модель Whisper не найдена. Укажи путь в настройках.");
        }

        if (CudaBootstrap.IsAvailable)
        {
            try
            {
                RuntimeOptions.RuntimeLibraryOrder = new List<RuntimeLibrary> { RuntimeLibrary.Cuda12 };
                var cuda = new WhisperTranscriber(_settings.ModelPath, _settings.Language);
                Log.Write("asr runtime: cuda12");
                return cuda;
            }
            catch (Exception ex)
            {
                Log.Write("cuda failed, falling back to CPU: " + ex.Message);
            }
        }

        RuntimeOptions.RuntimeLibraryOrder = new List<RuntimeLibrary> { RuntimeLibrary.Cpu };
        Log.Write("asr runtime: cpu");
        return new WhisperTranscriber(_settings.ModelPath, _settings.Language, _prompt);
    }

    private void ShowIndicator()
    {
        try
        {
            _indicator ??= new RecordingIndicator();
            _indicator.PlaceNearBottom();

            if (!_indicator.IsVisible)
            {
                _indicator.Show();
            }
        }
        catch (Exception ex)
        {
            Log.Write("indicator failed: " + ex.Message);
        }
    }

    private void HideIndicator()
    {
        try
        {
            if (_indicator is { IsVisible: true })
            {
                _indicator.Hide();
            }
        }
        catch (Exception)
        {
            // Indicator is cosmetic; never let it break dictation.
        }
    }

    private HotkeyPoller CreatePoller()
    {
        var poller = new HotkeyPoller(HotkeyDefinition.Parse(_settings.HotkeyPreset));
        poller.Pressed += OnPressed;
        poller.Released += OnReleased;
        return poller;
    }

    public async Task<BenchmarkResult> RunBenchmarkAsync()
    {
        _transcriber ??= CreateTranscriber();
        Log.Write("benchmark: started");
        var result = await BenchmarkService.RunAsync(_transcriber);
        Log.Write("benchmark: " + result.Summary);
        return result;
    }

    public async Task<BenchmarkService.ContextBenchmarkResult> RunContextBenchmarkAsync()
    {
        var (chunk, _) = ContextChunkSelector.SelectNext(_store.DataDirectory, _statsStore.Load().Recent.Select(entry => entry.Text));
        if (string.IsNullOrWhiteSpace(chunk))
        {
            throw new InvalidOperationException(
                "Недостаточно фраз: продиктуйте хотя бы одну фразу — контекстный бенчмарк проверяет ваши последние диктовки по 60 символов за прогон.");
        }

        _transcriber ??= CreateTranscriber();
        Log.Write($"context benchmark: started, chunk={chunk.Length} chars");
        var result = await BenchmarkService.RunContextAsync(_transcriber, new[] { chunk }, _corrector);
        Log.Write("context benchmark: " + result.Summary);
        return result;
    }

    /// <summary>
    /// Public wrapper over the edit-diff learner so the context benchmark
    /// window can remember the user's in-place corrections.
    /// </summary>
    public int LearnCorrection(string typed, string current) => ApplyCorrections(typed, current);

    public void ShowContextBenchmark()
    {
        var window = BuildContextBenchmarkWindow();
        window.ShowDialog();
    }

    private Window BuildContextBenchmarkWindow()
    {
        var window = new Window
        {
            Title = "Контекстный бенчмарк — ваши последние фразы",
            Width = 760,
            Height = 560,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        if (Application.Current.MainWindow is { } owner)
        {
            window.Owner = owner;
        }

        var muted = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6a, 0x6a, 0x76));
        var root = new System.Windows.Controls.DockPanel { Margin = new Thickness(14) };
        window.Content = root;

        var status = new System.Windows.Controls.TextBlock
        {
            Text = "Загружаю ваши фразы…",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
            Foreground = muted
        };

        var buttons = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal
        };
        System.Windows.Controls.DockPanel.SetDock(buttons, System.Windows.Controls.Dock.Bottom);
        var saveButton = new System.Windows.Controls.Button
        {
            Content = "Запомнить исправления",
            Width = 200,
            Height = 30,
            Margin = new Thickness(0, 8, 8, 0)
        };
        var closeButton = new System.Windows.Controls.Button
        {
            Content = "Закрыть",
            Width = 110,
            Height = 30,
            Margin = new Thickness(0, 8, 0, 0)
        };
        buttons.Children.Add(saveButton);
        buttons.Children.Add(closeButton);

        var scroll = new System.Windows.Controls.ScrollViewer { VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto };
        var rows = new System.Windows.Controls.StackPanel();
        scroll.Content = rows;
        root.Children.Add(buttons);
        root.Children.Add(scroll);

        var edits = new List<System.Windows.Controls.TextBox>();
        BenchmarkService.ContextBenchmarkResult result = null;

        async Task Reload()
        {
            saveButton.IsEnabled = false;
            closeButton.IsEnabled = false;
            status.Text = "Прогоняю ваши фразы через модель и словарь…";
            edits.Clear();
            rows.Children.Clear();

            try
            {
                result = await RunContextBenchmarkAsync();
            }
            catch (Exception ex)
            {
                status.Text = "✗ " + ex.Message;
                saveButton.IsEnabled = true;
                closeButton.IsEnabled = true;
                return;
            }

            status.Text = result.Summary + $"   ·   модель: {result.ModelName}, голос: {result.TtsVoice}";
            var note = new System.Windows.Controls.TextBlock
            {
                Text = "«Сказано» — что вы продиктовали ранее; в поле — как это распознаётся сейчас. Исправьте ошибки в полях и нажмите «Запомнить исправления»: словарь выучит замены, и следующий прогон покажет улучшение.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 10),
                Foreground = muted
            };
            rows.Children.Add(note);

            foreach (var row in result.Rows)
            {
                rows.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text = "Сказано: " + row.Reference,
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 10, 0, 4)
                });
                var edit = new System.Windows.Controls.TextBox
                {
                    Text = row.Corrected,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 4)
                };
                edits.Add(edit);
                rows.Children.Add(edit);
                if (row.Recognized != row.Corrected)
                {
                    rows.Children.Add(new System.Windows.Controls.TextBlock
                    {
                        Text = "до словаря: " + row.Recognized,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = muted
                    });
                }
            }

            saveButton.IsEnabled = true;
            closeButton.IsEnabled = true;
        }

        saveButton.Click += async (_, _) =>
        {
            if (result is null)
            {
                return;
            }

            saveButton.IsEnabled = false;
            var learned = 0;
            for (var i = 0; i < result.Rows.Count && i < edits.Count; i++)
            {
                learned += LearnCorrection(result.Rows[i].Reference, edits[i].Text);
            }

            MessageBox.Show(window,
                learned > 0
                    ? $"Выучено замен: {learned}. Запускаю повторный прогон — увидите, как словарь исправляет эти фразы."
                    : "Новых замен не найдено (исправления уже в словаре или слова совпадают). Запускаю повторный прогон.",
                "Контекстный бенчмарк",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            await Reload();
        };
        closeButton.Click += (_, _) => window.Close();
        window.Loaded += async (_, _) => await Reload();

        return window;
    }

    public void ShowOnboarding()
    {
        var wizard = new OnboardingWizard(_settings, _store);
        if (wizard.ShowDialog() != true)
        {
            return;
        }

        _hotkey.Update(HotkeyDefinition.Parse(_settings.HotkeyPreset));
        _transcriber?.Dispose();
        _transcriber = null;
        Log.Write($"onboarding done: model='{_settings.ModelPath}', hotkey={_settings.HotkeyPreset}, mic={_settings.AudioDevice} name='{_settings.AudioDeviceName}'");
    }

    public void ShowSettings()
    {
        var window = new SettingsWindow(_settings, _vocabulary, _statsStore.Load(), RunBenchmarkAsync, ShowContextBenchmark, _profiles);        if (window.ShowDialog() != true)
        {
            return;
        }

        _settings = window.Result;
        _store.Save(_settings);
        _cleanups.Clear();

        _hotkey.Update(HotkeyDefinition.Parse(_settings.HotkeyPreset));
        Log.Write("hotkey updated: " + _settings.HotkeyPreset);

        _vocabulary = window.ResultTerms;
        _vocabularyStore.Save(_vocabulary);

        _profiles = window.ResultProfiles;
        _profileStore.Save(_profiles);
        _corrector = new TermCorrector(_vocabulary);
        _prompt = BuildPrompt();

        Log.Write($"settings saved: model='{_settings.ModelPath}', lang={_settings.Language}, terms={_corrector.TermCount}");
        _transcriber?.Dispose();
        _transcriber = null;
    }

    /// <summary>
    /// Types the last completed dictation again into whatever window has focus.
    /// The text is kept even when the original SendInput was refused.
    /// </summary>
    private void PasteLast()
    {
        if (!_pasteLast.TryTake(out var text))
        {
            _tray.ShowInfo("Dictum", "Пока нет ни одной распознанной фразы.");
            return;
        }

        try
        {
            var target = _injector.CaptureTarget();
            var focused = _injector.Focus(target);
            var ok = _injector.TypeText(text + " ");
            Log.Write($"paste-last: chars={text.Length} focused={focused} ok={ok}");
            Diagnostics.Write("paste-last", ok ? "ok" : "blocked", 0,
                ("chars", text.Length), ("focused", focused));

            if (!ok)
            {
                _tray.ShowError("Dictum: текст не вставлен", "Окно отклонило ввод. Выберите нужное окно и повторите.");
            }
        }
        catch (Exception ex)
        {
            Log.Write("paste-last failed: " + ex.Message);
        }
    }

    private async Task<string> ApplyAiAsync(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

        if (!_settings.AiEnabled)
        {
            Log.Write($"ai: event=local reason=ai_disabled words={words}");
            return LocalPolisher.Polish(text);
        }

        if (words < _settings.AiMinWords)
        {
            Log.Write($"ai: event=local reason=below_min_words words={words} min={_settings.AiMinWords}");
            return LocalPolisher.Polish(text);
        }

        var (primary, primaryReason, primaryHttp) = await RunCleanupAsync(
            _settings.AiProvider, _settings.AiModel, _settings.AiBaseUrl, text);
        if (!string.IsNullOrWhiteSpace(primary))
        {
            var normalized = _corrector.Correct(primary);
            Log.Write($"ai: event=success provider={_settings.AiProvider} http={primaryHttp} words={words} chars={normalized.Length}");
            return normalized;
        }

        if (_settings.AiFallbackEnabled)
        {
            Log.Write($"ai: event=fallback from={_settings.AiProvider} to=gemini reason={primaryReason} http={primaryHttp} words={words}");
            var (fallback, fallbackReason, fallbackHttp) = await RunCleanupAsync(
                "gemini",
                _settings.AiFallbackModel,
                AiCleanup.DefaultEndpoint("gemini"),
                text);

            if (!string.IsNullOrWhiteSpace(fallback))
            {
                var normalized = _corrector.Correct(fallback);
                Log.Write($"ai: event=success provider=gemini http={fallbackHttp} fallback=true words={words} chars={normalized.Length}");
                return normalized;
            }

            Log.Write($"ai: event=local reason=both_providers_failed primary={primaryReason} fallback={fallbackReason} words={words}");
        }
        else
        {
            Log.Write($"ai: event=local reason=fallback_disabled primary={primaryReason} words={words}");
        }

        return LocalPolisher.Polish(text);
    }

    private async Task<(string? Text, string Reason, string Http)> RunCleanupAsync(
        string provider, string model, string endpoint, string text)
    {
        var cleanup = GetCleanup(provider, model, endpoint);
        if (!cleanup.IsConfigured)
        {
            Log.Write($"ai: event=not_configured provider={provider} http=-");
            return (null, "not_configured", "-");
        }

        var result = await cleanup.CleanAsync(text, _settings.AiTimeoutSeconds, BuildAiContext());
        var http = cleanup.LastHttpStatus?.ToString() ?? "-";
        var reason = result is null ? (cleanup.LastError ?? "empty_response") : "ok";
        return (result, reason, http);
    }

    private AiCleanup GetCleanup(string provider, string model, string endpoint)
    {
        var key = provider + "|" + model + "|" + endpoint;
        if (!_cleanups.TryGetValue(key, out var cleanup))
        {
            cleanup = new AiCleanup(provider, AiCleanup.ResolveKey(provider) ?? string.Empty, model, endpoint);
            _cleanups[key] = cleanup;
        }

        return cleanup;
    }

    private static int Words(string? text)
        => string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private string BuildPrompt()
    {
        var plan = PromptBudget.Build(
            _vocabulary.Select(term => term.Canonical),
            Math.Max(16, _settings.PromptMaxChars),
            Math.Max(1, _settings.PromptMaxTerms));

        Log.Write($"prompt: {plan.TermsRead} of {plan.TermsTotal} terms, {plan.Characters}/{plan.Budget} chars, cap={plan.TermCap}, truncated={plan.Truncated}");
        return plan.Text;
    }

    private static string? ReadFocusedText()
    {
        var element = AutomationElement.FocusedElement;
        if (element is null)
        {
            return null;
        }

        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePattern))
        {
            return ((ValuePattern)valuePattern).Current.Value;
        }

        if (element.TryGetCurrentPattern(TextPattern.Pattern, out var textPattern))
        {
            return ((TextPattern)textPattern).DocumentRange.GetText(-1);
        }

        return null;
    }

    /// <summary>
    /// Compares what we typed with what is in the field a few seconds later.
    /// A replaced word is treated as a correction and remembered as a variant
    /// of the new spelling ("жир" → "Jira").
    /// </summary>
    private int ApplyCorrections(string typed, string current)
    {
        var typedWords = typed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var currentWords = current.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (typedWords.Length == 0 || typedWords.Length != currentWords.Length)
        {
            return 0;
        }

        var learned = 0;

        for (var i = 0; i < typedWords.Length && learned < 2; i++)
        {
            var before = typedWords[i].Trim('.', ',', '!', '?', ';', ':');
            var after = currentWords[i].Trim('.', ',', '!', '?', ';', ':');

            if (before.Length < 2 || after.Length < 2) continue;
            if (string.Equals(before, after, StringComparison.Ordinal)) continue;
            if (before.Contains(' ') || after.Contains(' ')) continue;
            if (TermCorrector.Normalize(before) == TermCorrector.Normalize(after)) continue;

            var target = _vocabulary.FirstOrDefault(term =>
                string.Equals(TermCorrector.Normalize(term.Canonical), TermCorrector.Normalize(after), StringComparison.Ordinal));

            if (target is null)
            {
                target = new VocabularyTerm { Canonical = after };
                _vocabulary.Add(target);
            }

            if (target.Variants.Any(v => string.Equals(TermCorrector.Normalize(v), TermCorrector.Normalize(before), StringComparison.Ordinal)))
            {
                continue;
            }

            target.Variants.Add(before);
            Log.Write($"learned: 1 variant added to a vocabulary term ({before.Length} -> {target.Canonical.Length} chars)");
            learned++;
        }

        if (learned > 0)
        {
            _vocabularyStore.Save(_vocabulary);
            _corrector = new TermCorrector(_vocabulary);
            _prompt = BuildPrompt();
            _transcriber?.Dispose();
            _transcriber = null;
        }

        return learned;
    }

    private async void LearnFromEdits(string typed)
    {
        if (!_settings.LearnEnabled)
        {
            return;
        }

        try
        {
            await Task.Delay(4500);
            var current = ReadFocusedText();
            if (string.IsNullOrWhiteSpace(current) || current.Length > 4000)
            {
                return;
            }

            ApplyCorrections(typed, current);
        }
        catch (Exception ex)
        {
            Log.Write("learn failed: " + ex.Message);
        }
    }

    private void ShowAbout() => new MainWindow().ShowDialog();

    public void Dispose()
    {
        _hotkey.Dispose();
        _recorder.Dispose();
        _transcriber?.Dispose();
        _indicator?.Close();
        _tray.Dispose();
    }
}
