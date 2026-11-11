using System.Diagnostics;
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
        AiBox.IsChecked = settings.AiEnabled;
        AiModelBox.Text = settings.AiModel;
        AiBaseUrlBox.Text = settings.AiBaseUrl;
        AiFallbackBox.IsChecked = settings.AiFallbackEnabled;
        AiFallbackModelBox.Text = settings.AiFallbackModel;
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

        DashRecentText.Text = _stats.Recent.Count == 0
            ? "Пока пусто — продиктуй первую фразу."
            : string.Join(
                Environment.NewLine,
                _stats.Recent.Take(5).Select(entry => $"{entry.Time:HH:mm}   {entry.Text}"));
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
