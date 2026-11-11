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
    private readonly HotkeyPoller _hotkey = new();
    private readonly TrayIcon _tray = new();
    private readonly VocabularyStore _vocabularyStore = new();

    private List<VocabularyTerm> _vocabulary;
    private TermCorrector _corrector;
    private string _prompt;
    private AppSettings _settings;
    private WhisperTranscriber? _transcriber;
    private nint _targetWindow;
    private DateTime _recordedAt;
    private bool _busy;

    public AppController()
    {
        _settings = _store.Load();
        _vocabulary = _vocabularyStore.Load();
        _corrector = new TermCorrector(_vocabulary);
        _prompt = BuildPrompt();

        _hotkey.Pressed += OnPressed;
        _hotkey.Released += OnReleased;
        _tray.SettingsRequested += (_, _) => ShowSettings();
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
            Log.Write("model missing, opening settings");
            ShowSettings();
        }
    }

    private void OnPressed(object? sender, EventArgs e)
    {
        if (_busy)
        {
            return;
        }

        _targetWindow = _injector.CaptureTarget();
        _recordedAt = DateTime.UtcNow;
        _recorder.Start();
        Log.Write($"press, target=0x{_targetWindow.ToInt64():X}");
    }

    private async void OnReleased(object? sender, EventArgs e)
    {
        var pcm = _recorder.Stop();
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

        if ((DateTime.UtcNow - _recordedAt).TotalMilliseconds < _settings.MinRecordMs)
        {
            return;
        }

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

            _injector.Focus(_targetWindow);
            await Task.Delay(120);
            _injector.TypeText(corrected + " ");
            LearnFromEdits(corrected);
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
        return await _transcriber.TranscribeAsync(pcm);
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

    private void ShowSettings()
    {
        var window = new SettingsWindow(_settings, _vocabulary);
        if (window.ShowDialog() != true)
        {
            return;
        }

        _settings = window.Result;
        _store.Save(_settings);

        _vocabulary = window.ResultTerms;
        _vocabularyStore.Save(_vocabulary);
        _corrector = new TermCorrector(_vocabulary);
        _prompt = BuildPrompt();

        Log.Write($"settings saved: model='{_settings.ModelPath}', lang={_settings.Language}, terms={_corrector.TermCount}");
        _transcriber?.Dispose();
        _transcriber = null;
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
        _tray.Dispose();
    }
}
