using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Dictum.Audio;
using Dictum.Input;
using Dictum.Settings;
using Microsoft.Win32;

namespace Dictum.Views;

/// <summary>
/// Three-step first-run wizard: pick (and download) a model, choose the
/// push-to-talk chord, verify the microphone. Everything it changes is written
/// to settings.json when the user presses «Готово».
/// </summary>
public partial class OnboardingWizard : Window
{
    private static readonly string[] Titles =
    {
        "Добро пожаловать в Dictum",
        "Комбинация клавиш",
        "Проверка микрофона"
    };

    private static readonly string[] Hints =
    {
        "Шаг 1 из 3. Выберите модель — она скачается один раз и будет работать офлайн.",
        "Шаг 2 из 3. Выберите комбинацию, которой будете включать запись.",
        "Шаг 3 из 3. Убедимся, что микрофон слышит вас."
    };

    private readonly AppSettings _settings;
    private readonly SettingsStore _store;
    private readonly List<MicDevice> _micDevices = new();
    private bool _refreshingMics;
    private int _step;

    public OnboardingWizard(AppSettings settings, SettingsStore store)
    {
        InitializeComponent();

        _settings = settings;
        _store = store;

        ModelBox.SelectedIndex = 0;

        foreach (var preset in HotkeyDefinition.Presets)
        {
            HotkeyBox.Items.Add(new ComboBoxItem { Content = preset.Label, Tag = preset.Id });
        }

        var currentHotkey = Array.FindIndex(HotkeyDefinition.Presets, item => item.Id == settings.HotkeyPreset);
        HotkeyBox.SelectedIndex = currentHotkey >= 0 ? currentHotkey : 0;

        RefreshMicDevices();

        if (!string.IsNullOrWhiteSpace(settings.ModelPath) && File.Exists(settings.ModelPath))
        {
            ModelPathText.Text = "Текущая модель: " + settings.ModelPath;
        }

        UpdateStep();
    }

    private MicDevice? CurrentMic =>
        MicBox.SelectedIndex >= 0 && MicBox.SelectedIndex < _micDevices.Count
            ? _micDevices[MicBox.SelectedIndex]
            : null;

    /// <summary>
    /// Re-reads the device list. Called on construction, every time step 3 is
    /// shown, and by the explicit «Обновить список» button. Windows can add or
    /// remove a microphone while the wizard is open, so a one-shot enumeration
    /// in the constructor used to leave the dropdown permanently empty.
    /// </summary>
    private void RefreshMicDevices()
    {
        _refreshingMics = true;
        try
        {
            var devices = MicDeviceCatalog.Enumerate();
            var savedName = _settings.AudioDeviceName?.Trim() ?? string.Empty;
            var savedIndex = _settings.AudioDevice;

            _micDevices.Clear();
            _micDevices.AddRange(devices);

            MicBox.Items.Clear();
            foreach (var device in _micDevices)
            {
                MicBox.Items.Add(device.Display);
            }

            var selected = -1;
            if (savedName.Length > 0)
            {
                selected = _micDevices.FindIndex(d =>
                    string.Equals(d.Name, savedName, StringComparison.OrdinalIgnoreCase));
            }

            if (selected < 0 && savedIndex >= 0)
            {
                selected = _micDevices.FindIndex(d => d.Usable && d.Index == savedIndex);
            }

            if (selected < 0)
            {
                selected = _micDevices.FindIndex(d => d.Usable);
            }

            // Even when nothing is usable, select the first disabled/unplugged
            // entry so the user sees the reason rather than a blank ComboBox.
            if (selected < 0 && _micDevices.Count > 0)
            {
                selected = 0;
            }

            MicBox.SelectedIndex = selected;
        }
        finally
        {
            _refreshingMics = false;
        }

        UpdateMicState();
    }

    private void ApplyMicSelection()
    {
        var device = CurrentMic;
        if (device is { Usable: true })
        {
            _settings.AudioDevice = device.Index;
            _settings.AudioDeviceName = device.Name;
        }
    }

    private void UpdateMicState()
    {
        var usable = CurrentMic is { Usable: true };
        MicTestButton.IsEnabled = usable;

        if (_micDevices.Count == 0)
        {
            MicStatus.Text = "Windows не видит ни одного устройства записи. "
                + MicDeviceCatalog.Hint
                + ". После включения нажмите «Обновить список».";
        }
        else if (!usable)
        {
            MicStatus.Text = "Устройства найдены, но ни одно не активно: "
                + string.Join("; ", _micDevices.Select(d => d.Display)) + ". "
                + MicDeviceCatalog.Hint
                + ", затем нажмите «Обновить список». Настройку можно завершить и позже.";
        }
        else
        {
            MicStatus.Text = string.Empty;
        }
    }

    private void OnRefreshMic(object sender, RoutedEventArgs e)
    {
        RefreshMicDevices();
    }

    private void OnMicSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_refreshingMics)
        {
            UpdateMicState();
        }
    }

    private void UpdateStep()
    {
        if (_step == 2)
        {
            RefreshMicDevices();
        }
        StepTitle.Text = Titles[_step];
        StepHint.Text = Hints[_step];

        PanelModel.Visibility = _step == 0 ? Visibility.Visible : Visibility.Collapsed;
        PanelHotkey.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        PanelMic.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;

        BackButton.IsEnabled = _step > 0;
        NextButton.Content = _step == 2 ? "Готово" : "Далее";
    }

    private async void OnNext(object sender, RoutedEventArgs e)
    {
        if (_step == 0)
        {
            if (string.IsNullOrWhiteSpace(_settings.ModelPath) || !File.Exists(_settings.ModelPath))
            {
                if (!await EnsureModelAsync())
                {
                    return;
                }
            }

            _step = 1;
            UpdateStep();
            return;
        }

        if (_step == 1)
        {
            _settings.HotkeyPreset = (HotkeyBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "ctrl+win";

            ApplyMicSelection();

            _step = 2;
            UpdateStep();
            return;
        }

        // The list may have been refreshed on step 3 after the user enabled
        // the microphone in Windows, so re-apply the selection before saving.
        ApplyMicSelection();
        _store.Save(_settings);
        DialogResult = true;
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (_step == 0)
        {
            return;
        }

        _step--;
        UpdateStep();
    }

    private async Task<bool> EnsureModelAsync()
    {
        var file = (ModelBox.SelectedItem as ComboBoxItem)?.Tag as string;
        if (string.IsNullOrWhiteSpace(file))
        {
            return false;
        }

        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Dictum",
            "models");
        Directory.CreateDirectory(directory);

        var destination = Path.Combine(directory, file);

        if (File.Exists(destination) && new FileInfo(destination).Length > 1_000_000)
        {
            _settings.ModelPath = destination;
            ModelStatus.Text = "Эта модель уже скачана.";
            return true;
        }

        NextButton.IsEnabled = false;
        BackButton.IsEnabled = false;
        PickFileButton.IsEnabled = false;
        ModelProgress.Visibility = Visibility.Visible;
        ModelProgress.Value = 0;
        ModelStatus.Text = "Начинаю загрузку…";

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            var url = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/" + file;

            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? 0;
            var buffer = new byte[81920];
            long read = 0;

            await using var source = await response.Content.ReadAsStreamAsync();
            await using var target = File.Create(destination);

            int chunk;
            while ((chunk = await source.ReadAsync(buffer)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, chunk));
                read += chunk;

                ModelProgress.Value = total > 0 ? read * 100.0 / total : 0;
                ModelStatus.Text = total > 0
                    ? $"Скачано {read / 1048576.0:F0} из {total / 1048576.0:F0} МБ"
                    : $"Скачано {read / 1048576.0:F0} МБ";
            }

            _settings.ModelPath = destination;
            ModelStatus.Text = "Готово.";
            ModelPathText.Text = "Модель: " + destination;
            return true;
        }
        catch (Exception ex)
        {
            ModelStatus.Text = "Не удалось скачать: " + ex.Message + ". Можно указать файл вручную.";
            return false;
        }
        finally
        {
            NextButton.IsEnabled = true;
            BackButton.IsEnabled = _step > 0;
            PickFileButton.IsEnabled = true;
            ModelProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void OnPickFile(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выбери модель Whisper",
            Filter = "Whisper model (*.bin)|*.bin|Все файлы (*.*)|*.*"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _settings.ModelPath = dialog.FileName;
        ModelPathText.Text = "Модель: " + dialog.FileName;
        ModelStatus.Text = string.Empty;
    }

    private async void OnMicTest(object sender, RoutedEventArgs e)
    {
        var mic = CurrentMic;
        if (mic is not { Usable: true })
        {
            MicStatus.Text = "Сначала выберите активный микрофон из списка. "
                + MicDeviceCatalog.Hint + ", затем нажмите «Обновить список».";
            return;
        }

        MicTestButton.IsEnabled = false;
        MicProgress.Visibility = Visibility.Visible;
        MicStatus.Text = "Говорите…";

        try
        {
            using var recorder = new AudioRecorder();
            recorder.Start(mic.Index);
            await Task.Delay(2500);
            var pcm = recorder.Stop();

            var peak = 0;
            for (var i = 0; i + 1 < pcm.Length; i += 2)
            {
                var sample = Math.Abs((int)BitConverter.ToInt16(pcm, i));
                if (sample > peak)
                {
                    peak = sample;
                }
            }

            var level = peak / 32768.0;
            MicStatus.Text = level > 0.05
                ? $"Слышу вас (уровень {level:P0}). Всё готово."
                : "Очень тихо. Проверьте, что микрофон не заглушён и выбран правильный вход.";
        }
        catch (Exception ex)
        {
            MicStatus.Text = "Ошибка записи: " + ex.Message;
        }
        finally
        {
            MicTestButton.IsEnabled = true;
            MicProgress.Visibility = Visibility.Collapsed;
        }
    }
}
