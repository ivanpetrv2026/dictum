using System.Windows;
using System.Windows.Controls;
using Dictum.Services;
using Dictum.Settings;
using Dictum.Vocabulary;
using Microsoft.Win32;

namespace Dictum.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly List<VocabularyTerm> _originalTerms;
    private readonly bool _initializing;

    public SettingsWindow(AppSettings settings, IEnumerable<VocabularyTerm> terms)
    {
        InitializeComponent();
        _initializing = true;

        _settings = settings;
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
    }

    public AppSettings Result => _settings;

    public List<VocabularyTerm> ResultTerms { get; private set; } = new();

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
            ? "anthropic/claude-haiku-4.5"
            : AiModelBox.Text.Trim();
        _settings.AiBaseUrl = string.IsNullOrWhiteSpace(AiBaseUrlBox.Text)
            ? AiCleanup.DefaultEndpoint(_settings.AiProvider)
            : AiBaseUrlBox.Text.Trim();

        ResultTerms = ParseTerms(TermsBox.Text);
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
