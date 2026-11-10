using System.IO;
using System.Text;
using Whisper.net;

namespace Dictum.Speech;

/// <summary>
/// Thin wrapper over whisper.cpp (via Whisper.net). Model is loaded once and
/// kept in memory; recognition runs fully offline.
/// </summary>
public sealed class WhisperTranscriber : IDisposable
{
    private readonly WhisperFactory _factory;
    private readonly string _language;

    public WhisperTranscriber(string modelPath, string language)
    {
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException("Файл модели Whisper не найден.", modelPath);
        }

        _factory = WhisperFactory.FromPath(modelPath);
        _language = string.IsNullOrWhiteSpace(language) ? "auto" : language;
    }

    public async Task<string> TranscribeAsync(byte[] pcm16KhzMono, CancellationToken cancellationToken = default)
    {
        if (pcm16KhzMono.Length == 0)
        {
            return string.Empty;
        }

        using var processor = _factory.CreateBuilder()
            .WithLanguage(_language)
            .Build();

        using var stream = new MemoryStream(pcm16KhzMono);
        var text = new StringBuilder();

        await foreach (var segment in processor.ProcessAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            text.Append(segment.Text);
        }

        return text.ToString().Trim();
    }

    public void Dispose() => _factory.Dispose();
}
