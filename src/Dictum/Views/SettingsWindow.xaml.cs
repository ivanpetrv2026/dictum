using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Dictum.Services;
using Dictum.Settings;
using Dictum.Vocabulary;
using Microsoft.Win32;

namespace Dictum.Views;

public partial class SettingsWindow : Window
{
    private static readonly (string Tag, string Name)[] Providers =
    {
        ("openrouter", "OpenRouter"),
        ("gemini", "Gemini"),
        ("orcarouter", "OrcaRouter"),
        ("teamorouter", "TeamRouter"),
        ("bynara", "Bynara")
    };

    private readonly AppSettings _settings;
    private readonly List<VocabularyTerm> _originalTerms;
    private readonly StatsSnapshot _stats;
    private readonly bool _initializing;

    public SettingsWindow(AppSettings settings, IEnumerable<VocabularyTerm> terms, StatsSnapshot stats)
    {
        InitializeComponent();
        _initializing = true;

        _settings = settings;
        _stats = stats;
        _originalTerms = terms
            .Select(term => new VocabularyTerm
            {
                Canonical = term.Canonical,
                Variants = new List<string>(term.Variants)
            })
            .ToList();

        ModelPathBox.Text = settings.ModelPath;
        LearnBox.IsChecked = settings.LearnEnabled;
        SaveTranscriptsBox.IsChecked = settings.SaveTranscripts;
        AiBox.IsChecked = settings.AiEnabled;
        AiModelBox.Text = settings.AiModel;
        AiBaseUrlBox.Text = settings.AiBaseUrl;
        AiFallbackBox.IsChecked = settings.AiFallbackEnabled;
        AiFallbackModelBox.Text = settings.AiFallbackModel;
        AiModelBox.ItemsSource = AiCleanup.ModelsFor(settings.AiProvider);
        AiModelBox.Text = settings.AiModel;
        TermsBox.Text = FormatTerms(_originalTerms);

        foreach (ComboBoxItem item in AiProviderBox.Items)
        {
            if (string.Equals(item.Tag as string, settings.AiProvider, StringComparison.OrdinalIgnoreCase))
            {
                AiProviderBox.SelectedItem = item;
                break;
            }
        }

        AiProviderBox.SelectedItem ??= AiProviderBox.Items[0];

        foreach (ComboBoxItem item in LanguageBox.Items)
        {
            if (string.Equals(item.Content as string, settings.Language, StringComparison.OrdinalIgnoreCase))
            {
                LanguageBox.SelectedItem = item;
                break;
            }
        }

        LanguageBox.SelectedItem ??= LanguageBox.Items[0];

        _initializing = false;
        UpdateDashboard();
        BuildModelsList();
        BuildKeysList();
    }

    public AppSettings Result => _settings;

    public List<VocabularyTerm> ResultTerms { get; private set; } = new();

    private static string ProviderName(string tag) =>
        Providers.FirstOrDefault(provider => provider.Tag == tag).Name ?? tag;

    private void UpdateDashboard()
    {
        var version = typeof(Dictum.App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        DashSubtitle.Text = $"Локальная диктовка · v{version} · {_originalTerms.Count} терминов";

        DashToday.Text = _stats.Today.ToString();
        DashTotal.Text = _stats.Total.ToString();
        DashGateway.Text = ProviderName(_settings.AiProvider);

        var keys = Providers.Select(provider =>
        {
            var key = AiCleanup.ResolveKey(provider.Tag);
            return $"{provider.Name} {(string.IsNullOrWhiteSpace(key) ? "—" : "✓")}";
        });

        var line = string.Join("     ", keys);
        if (_settings.AiFallbackEnabled)
        {
            var geminiKey = AiCleanup.ResolveKey("gemini");
            line += $"     ·     резерв Gemini {(string.IsNullOrWhiteSpace(geminiKey) ? "—" : "✓")}";
        }

        DashKeysText.Text = line;

        TranscriptsPathText.Text = new TranscriptStore().Directory;

        DashRecentText.Text = _stats.Recent.Count == 0
            ? "Пока пусто — продиктуй первую фразу."
            : string.Join(
                Environment.NewLine,
                _stats.Recent.Take(5).Select(entry => $"{entry.Time:HH:mm}   {entry.Text}"));
    }

    private sealed class KeyRow
    {
        public string Provider { get; init; } = string.Empty;

        public PasswordBox Box { get; init; } = null!;

        public TextBlock Status { get; init; } = null!;
    }

    private void BuildKeysList()
    {
        KeysPanel.Children.Clear();
        var muted = (System.Windows.Media.Brush)FindResource("TextMutedBrush");

        foreach (var provider in Providers)
        {
            var target = AiCleanup.CredentialTarget(provider.Tag);
            var hasKey = !string.IsNullOrWhiteSpace(CredentialManager.Read(target));

            var box = new PasswordBox { Width = 320, Height = 30, Padding = new Thickness(6, 4, 6, 4) };
            var status = new TextBlock { Foreground = muted, FontSize = 12, Margin = new Thickness(0, 6, 0, 0) };
            var row = new KeyRow { Provider = provider.Tag, Box = box, Status = status };

            var save = new Button { Width = 110, Margin = new Thickness(8, 0, 0, 0), Content = "Сохранить", Tag = row };
            save.Click += OnSaveKey;

            var check = new Button { Width = 110, Margin = new Thickness(8, 0, 0, 0), Content = "Проверить", Tag = row };
            check.Click += OnCheckKey;

            var inputRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            inputRow.Children.Add(box);
            inputRow.Children.Add(save);
            inputRow.Children.Add(check);

            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = provider.Name, FontWeight = FontWeights.SemiBold, FontSize = 14 });
            stack.Children.Add(new TextBlock
            {
                Text = hasKey ? "ключ сохранён в Credential Manager" : "ключ не задан",
                Foreground = muted,
                FontSize = 12,
                Margin = new Thickness(0, 2, 0, 0)
            });
            stack.Children.Add(inputRow);
            stack.Children.Add(status);

            KeysPanel.Children.Add(new Border
            {
                Padding = new Thickness(14),
                Margin = new Thickness(0, 0, 0, 8),
                Background = (System.Windows.Media.Brush)FindResource("SurfaceBrush"),
                CornerRadius = new CornerRadius(10),
                Child = stack
            });
        }
    }

    private void OnSaveKey(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not KeyRow row)
        {
            return;
        }

        var key = row.Box.Password.Trim();
        if (key.Length == 0)
        {
            row.Status.Text = "Введите ключ.";
            return;
        }

        var saved = CredentialManager.Write(AiCleanup.CredentialTarget(row.Provider), key);
        row.Status.Text = saved
            ? "Ключ сохранён. Нажмите «Сохранить» внизу, чтобы применить."
            : "Не удалось сохранить ключ (см. dictum.log).";

        if (saved)
        {
            row.Box.Clear();
        }
    }

    private async void OnCheckKey(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not KeyRow row)
        {
            return;
        }

        var key = row.Box.Password.Trim();
        if (key.Length == 0)
        {
            key = CredentialManager.Read(AiCleanup.CredentialTarget(row.Provider)) ?? string.Empty;
        }

        row.Status.Text = "Проверяю…";
        var (ok, message) = await AiCleanup.ProbeAsync(row.Provider, key);
        row.Status.Text = ok ? "Шлюз ответил — ключ рабочий." : "Ошибка: " + message;
    }

    private void BuildModelsList()
    {
        ModelsPanel.Children.Clear();
        var muted = (System.Windows.Media.Brush)FindResource("TextMutedBrush");

        foreach (var model in ModelCatalog.Models)
        {
            var installed = ModelCatalog.IsInstalled(model);
            var selected = string.Equals(_settings.ModelPath, ModelCatalog.PathFor(model), StringComparison.OrdinalIgnoreCase);

            var text = new StackPanel();
            text.Children.Add(new TextBlock
            {
                Text = model.Title,
                FontWeight = FontWeights.SemiBold,
                FontSize = 14
            });
            text.Children.Add(new TextBlock
            {
                Text = (installed ? "скачана" : "не скачана") + "   ·   " + model.SizeText + (selected ? "   ·   используется" : string.Empty),
                Foreground = muted,
                FontSize = 12,
                Margin = new Thickness(0, 4, 0, 0)
            });

            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            if (!installed)
            {
                var download = new Button { Content = "Скачать", Width = 110, Tag = model };
                download.Click += OnDownloadModel;
                actions.Children.Add(download);
            }
            else
            {
                if (!selected)
                {
                    var use = new Button { Content = "Использовать", Width = 120, Tag = model };
                    use.Click += OnUseModel;
                    actions.Children.Add(use);
                }

                var remove = new Button { Content = "Удалить", Width = 100, Margin = new Thickness(8, 0, 0, 0), Tag = model };
                remove.Click += OnDeleteModel;
                actions.Children.Add(remove);
            }

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(text, 0);
            Grid.SetColumn(actions, 1);
            grid.Children.Add(text);
            grid.Children.Add(actions);

            ModelsPanel.Children.Add(new Border
            {
                Padding = new Thickness(14),
                Margin = new Thickness(0, 0, 0, 8),
                Background = (System.Windows.Media.Brush)FindResource("SurfaceBrush"),
                CornerRadius = new CornerRadius(10),
                Child = grid
            });
        }
    }

    private async void OnDownloadModel(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ModelInfo model)
        {
            return;
        }

        ModelProgress.Visibility = Visibility.Visible;
        ModelProgress.Value = 0;
        ModelStatus.Text = "Начинаю загрузку…";

        var progress = new Progress<(long Read, long Total)>(value =>
        {
            ModelProgress.Value = value.Total > 0 ? value.Read * 100.0 / value.Total : 0;
            ModelStatus.Text = value.Total > 0
                ? $"Скачано {value.Read / 1048576.0:F0} из {value.Total / 1048576.0:F0} МБ"
                : $"Скачано {value.Read / 1048576.0:F0} МБ";
        });

        try
        {
            await ModelCatalog.DownloadAsync(model, progress, CancellationToken.None);
            _settings.ModelPath = ModelCatalog.PathFor(model);
            ModelStatus.Text = "Готово: модель скачана и выбрана.";
        }
        catch (Exception ex)
        {
            ModelStatus.Text = "Ошибка загрузки: " + ex.Message;
        }
        finally
        {
            ModelProgress.Visibility = Visibility.Collapsed;
            BuildModelsList();
        }
    }

    private void OnUseModel(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ModelInfo model)
        {
            return;
        }

        _settings.ModelPath = ModelCatalog.PathFor(model);
        ModelStatus.Text = "Выбрана модель: " + model.Title;
        BuildModelsList();
    }

    private void OnDeleteModel(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ModelInfo model)
        {
            return;
        }

        var isCurrent = string.Equals(_settings.ModelPath, ModelCatalog.PathFor(model), StringComparison.OrdinalIgnoreCase);
        var answer = MessageBox.Show(
            $"Удалить файл модели «{model.Title}»?" + (isCurrent ? "\n\nОна используется сейчас — после удаления нужно будет выбрать другую." : string.Empty),
            "Удаление модели",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            File.Delete(ModelCatalog.PathFor(model));
            if (isCurrent)
            {
                _settings.ModelPath = string.Empty;
            }

            ModelStatus.Text = "Модель удалена.";
        }
        catch (Exception ex)
        {
            ModelStatus.Text = "Не удалось удалить: " + ex.Message;
        }

        BuildModelsList();
    }

    private static string FormatTerms(IEnumerable<VocabularyTerm> terms) =>
        string.Join(
            Environment.NewLine,
            terms.Select(term => term.Variants.Count == 0
                ? term.Canonical
                : $"{term.Canonical} | {string.Join(", ", term.Variants)}"));

    private List<VocabularyTerm> ParseTerms(string text)
    {
        var known = _originalTerms.ToDictionary(
            term => TermCorrector.Normalize(term.Canonical),
            term => term,
            StringComparer.Ordinal);

        var parsed = new List<VocabularyTerm>();

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split('|', 2);
            var canonical = parts[0].Trim();
            if (canonical.Length == 0)
            {
                continue;
            }

            var variants = new List<string>();
            if (parts.Length == 2)
            {
                variants.AddRange(parts[1]
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(value => value.Trim())
                    .Where(value => value.Length > 0));
            }

            if (known.TryGetValue(TermCorrector.Normalize(canonical), out var existing))
            {
                foreach (var variant in existing.Variants)
                {
                    if (!variants.Any(v => string.Equals(TermCorrector.Normalize(v), TermCorrector.Normalize(variant), StringComparison.Ordinal)))
                    {
                        variants.Add(variant);
                    }
                }
            }

            parsed.Add(new VocabularyTerm { Canonical = canonical, Variants = variants });
        }

        return parsed;
    }

    private void OnOpenTranscripts(object sender, RoutedEventArgs e)
    {
        try
        {
            var directory = new TranscriptStore().Directory;
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Не удалось открыть папку", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        var provider = (AiProviderBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "openrouter";
        if (provider == "custom")
        {
            return;
        }

        AiBaseUrlBox.Text = AiCleanup.DefaultEndpoint(provider);

        var models = AiCleanup.ModelsFor(provider);
        AiModelBox.ItemsSource = models;
        AiModelBox.Text = AiCleanup.DefaultModel(provider);
    }

    private async void OnTestClick(object sender, RoutedEventArgs e)
    {
        TestButton.IsEnabled = false;
        TestResultText.Text = "Проверяю…";

        const string sample = "привет это проверка связи мы используем click house и github";

        try
        {
            var provider = (AiProviderBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "openrouter";
            var model = string.IsNullOrWhiteSpace(AiModelBox.Text) ? AiCleanup.DefaultModel(provider) : AiModelBox.Text.Trim();
            var endpoint = string.IsNullOrWhiteSpace(AiBaseUrlBox.Text) ? AiCleanup.DefaultEndpoint(provider) : AiBaseUrlBox.Text.Trim();

            var stopwatch = Stopwatch.StartNew();
            var primary = new AiCleanup(provider, AiCleanup.ResolveKey(provider) ?? string.Empty, model, endpoint);
            var result = await primary.CleanAsync(sample, 15);
            stopwatch.Stop();

            if (!string.IsNullOrWhiteSpace(result))
            {
                TestResultText.Text = $"✓ {ProviderName(provider)}: {stopwatch.Elapsed.TotalSeconds:F1} с — «{result}»";
                return;
            }

            if (AiFallbackBox.IsChecked == true)
            {
                var fallback = new AiCleanup(
                    "gemini",
                    AiCleanup.ResolveKey("gemini") ?? string.Empty,
                    string.IsNullOrWhiteSpace(AiFallbackModelBox.Text) ? "gemini-3-flash-preview" : AiFallbackModelBox.Text.Trim(),
                    AiCleanup.DefaultEndpoint("gemini"));

                var fallbackWatch = Stopwatch.StartNew();
                var fallbackResult = await fallback.CleanAsync(sample, 15);
                fallbackWatch.Stop();

                TestResultText.Text = !string.IsNullOrWhiteSpace(fallbackResult)
                    ? $"основной не ответил; резерв Gemini: {fallbackWatch.Elapsed.TotalSeconds:F1} с — «{fallbackResult}»"
                    : "✗ ни основной, ни резервный шлюз не ответили (смотри dictum.log)";
                return;
            }

            TestResultText.Text = "✗ шлюз не ответил (смотри dictum.log)";
        }
        catch (Exception ex)
        {
            TestResultText.Text = "✗ " + ex.Message;
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выбери модель Whisper",
            Filter = "Whisper model (*.bin)|*.bin|Все файлы (*.*)|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            ModelPathBox.Text = dialog.FileName;
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _settings.ModelPath = ModelPathBox.Text.Trim();
        _settings.Language = (LanguageBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "ru";
        _settings.LearnEnabled = LearnBox.IsChecked == true;
        _settings.SaveTranscripts = SaveTranscriptsBox.IsChecked == true;
        _settings.AiEnabled = AiBox.IsChecked == true;
        _settings.AiProvider = (AiProviderBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "openrouter";
        _settings.AiModel = string.IsNullOrWhiteSpace(AiModelBox.Text)
            ? AiCleanup.DefaultModel(_settings.AiProvider)
            : AiModelBox.Text.Trim();
        _settings.AiBaseUrl = string.IsNullOrWhiteSpace(AiBaseUrlBox.Text)
            ? AiCleanup.DefaultEndpoint(_settings.AiProvider)
            : AiBaseUrlBox.Text.Trim();
        _settings.AiFallbackEnabled = AiFallbackBox.IsChecked == true;
        _settings.AiFallbackModel = string.IsNullOrWhiteSpace(AiFallbackModelBox.Text)
            ? "gemini-3-flash-preview"
            : AiFallbackModelBox.Text.Trim();

        ResultTerms = ParseTerms(TermsBox.Text);
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
