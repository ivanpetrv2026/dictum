using System.Windows;
using System.Windows.Controls;
using Dictum.Settings;
using Microsoft.Win32;

namespace Dictum.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        ModelPathBox.Text = settings.ModelPath;

        foreach (ComboBoxItem item in LanguageBox.Items)
        {
            if (string.Equals(item.Content as string, settings.Language, StringComparison.OrdinalIgnoreCase))
            {
                LanguageBox.SelectedItem = item;
                break;
            }
        }

        LanguageBox.SelectedItem ??= LanguageBox.Items[0];
    }

    public AppSettings Result => _settings;

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
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
