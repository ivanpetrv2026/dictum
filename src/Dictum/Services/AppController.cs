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

        try
        {
            _recorder.Start(_settings.AudioDevice);
            ShowIndicator();
        }
        catch (Exception ex)
        {
            Log.Write("record start failed: " + ex.Message);
            HideIndicator();
            return;
        }

        Log.Write($"press, target=0x{_targetWindow.ToInt64():X}");
    }

    private DateTime _lastHumNoticeUtc = DateTime.MinValue;

    /// <summary>
    /// A silently skipped take looks like the app doing nothing, so surface
    /// the broken input once in a while instead of only in the log.
    /// </summary>
    private void NotifyHummingInput()
    {
        var now = DateTime.UtcNow;
        if (now - _lastHumNoticeUtc < TimeSpan.FromMinutes(10))
        {
            return;
        }

        _lastHumNoticeUtc = now;
        _tray.ShowError(
            "Dictum: микрофон не подаёт речь",
            "Записан только фоновый гул без речи. Проверьте штекер и кабель микрофона, попробуйте другой разъём.");
    }

    private async void OnReleased(object? sender, EventArgs e)
    {
        var pcm = _recorder.Stop();
        HideIndicator();
        var elapsed = (DateTime.UtcNow - _recordedAt).TotalMilliseconds;
        Log.Write($"release, bytes={pcm.Length}, duration={elapsed:F0}ms");

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
        if (AudioStats.LooksLikeHum(stats))
        {
            Log.Write($"audio: looks like a disconnected/humming input (rms={stats.Rms:F0}, zcr={stats.Zcr:F3}, flatness={stats.Flatness:F1}), skipped");
            NotifyHummingInput();
            return;
        }

        var (speechStart, speechEnd) = Vad.FindSpeech(pcm);
        var speechSamples = speechEnd - speechStart;

        if (speechSamples < 16_000 * 0.25)
        {
            // Very quiet recordings can defeat the detector; if there is still
            // measurable energy, let the model try instead of dropping the take.
            if (peak < 800)
            {
                Log.Write($"vad: no speech in {pcm.Length / 32000.0:F1}s (peak {peak}), skipped");
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

        _busy = true;
        try
        {
            var text = await TranscribeAsync(pcm);
            Log.Write($"asr: {text.Length} chars: '{text}'");
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            var corrected = _corrector.Correct(text);
            if (!string.Equals(corrected, text, StringComparison.Ordinal))
            {
                Log.Write($"corrected: '{corrected}'");
            }

            var final = await ApplyAiAsync(corrected);

            _injector.Focus(_targetWindow);
            await Task.Delay(120);
            _injector.TypeText(final + " ");
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
        Log.Write($"onboarding done: model='{_settings.ModelPath}', hotkey={_settings.HotkeyPreset}, mic={_settings.AudioDevice}");
    }

    public void ShowSettings()
    {
        var window = new SettingsWindow(_settings, _vocabulary, _statsStore.Load(), RunBenchmarkAsync, _profiles);        if (window.ShowDialog() != true)
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

    private async Task<string> ApplyAiAsync(string text)
    {
        if (!_settings.AiEnabled)
        {
            return LocalPolisher.Polish(text);
        }

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (words < _settings.AiMinWords)
        {
            return LocalPolisher.Polish(text);
        }

        var primary = await RunCleanupAsync(_settings.AiProvider, _settings.AiModel, _settings.AiBaseUrl, text);
        if (!string.IsNullOrWhiteSpace(primary))
        {
            var normalized = _corrector.Correct(primary);
            Log.Write($"ai: {words} words -> '{normalized}'");
            return normalized;
        }

        if (_settings.AiFallbackEnabled)
        {
            var fallback = await RunCleanupAsync(
                "gemini",
                _settings.AiFallbackModel,
                AiCleanup.DefaultEndpoint("gemini"),
                text);

            if (!string.IsNullOrWhiteSpace(fallback))
            {
                var normalized = _corrector.Correct(fallback);
                Log.Write($"ai (fallback gemini): {words} words -> '{normalized}'");
                return normalized;
            }
        }

        return LocalPolisher.Polish(text);
    }

    private async Task<string?> RunCleanupAsync(string provider, string model, string endpoint, string text)
    {
        var cleanup = GetCleanup(provider, model, endpoint);
        if (!cleanup.IsConfigured)
        {
            Log.Write($"ai: {provider} is not configured");
            return null;
        }

        return await cleanup.CleanAsync(text, _settings.AiTimeoutSeconds, BuildAiContext());
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

    private string BuildPrompt()
    {
        var canonical = _vocabulary
            .Select(term => term.Canonical)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Take(30);

        return string.Join(", ", canonical) + ".";
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
            Log.Write($"learned: '{before}' -> '{target.Canonical}'");
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
