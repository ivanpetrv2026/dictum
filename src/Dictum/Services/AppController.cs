using System.IO;
using System.Windows;
using Dictum.Audio;
using Dictum.Input;
using Dictum.Settings;
using Dictum.Speech;
using Dictum.Tray;
using Dictum.Views;

namespace Dictum.Services;

public sealed class AppController : IDisposable
{
    private readonly SettingsStore _store = new();
    private readonly AudioRecorder _recorder = new();
    private readonly TextInjector _injector = new();
    private readonly HotkeyPoller _hotkey = new();
    private readonly TrayIcon _tray = new();

    private AppSettings _settings;
    private WhisperTranscriber? _transcriber;
    private nint _targetWindow;
    private DateTime _recordedAt;
    private bool _busy;

    public AppController()
    {
        _settings = _store.Load();

        _hotkey.Pressed += OnPressed;
        _hotkey.Released += OnReleased;
        _tray.SettingsRequested += (_, _) => ShowSettings();
        _tray.AboutRequested += (_, _) => ShowAbout();
        _tray.ExitRequested += (_, _) => Application.Current.Shutdown();
    }

    public void Start()
    {
        Log.Write($"started, model='{_settings.ModelPath}', lang={_settings.Language}");
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

            _injector.Focus(_targetWindow);
            await Task.Delay(120);
            _injector.TypeText(text + " ");
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

        return new WhisperTranscriber(_settings.ModelPath, _settings.Language);
    }

    private void ShowSettings()
    {
        var window = new SettingsWindow(_settings);
        if (window.ShowDialog() != true)
        {
            return;
        }

        _settings = window.Result;
        _store.Save(_settings);
        Log.Write($"settings saved: model='{_settings.ModelPath}', lang={_settings.Language}");
        _transcriber?.Dispose();
        _transcriber = null;
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
