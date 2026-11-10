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

        using var stream = WrapAsWav(pcm16KhzMono);
        var text = new StringBuilder();

        await foreach (var segment in processor.ProcessAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            text.Append(segment.Text);
        }

        return text.ToString().Trim();
    }

    public void Dispose() => _factory.Dispose();

    /// <summary>
    /// Whisper.net parses the input stream as a WAV container (RIFF header),
    /// so raw PCM has to be wrapped before it is handed to the processor.
    /// </summary>
    private static MemoryStream WrapAsWav(byte[] pcm, int sampleRate = 16000, short channels = 1, short bitsPerSample = 16)
    {
        const int headerSize = 44;
        var stream = new MemoryStream(headerSize + pcm.Length);
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
        {
            var blockAlign = (short)(channels * bitsPerSample / 8);
            var byteRate = sampleRate * blockAlign;

            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + pcm.Length);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1); // PCM
            writer.Write(channels);
            writer.Write(sampleRate);
            writer.Write(byteRate);
            writer.Write(blockAlign);
            writer.Write(bitsPerSample);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(pcm.Length);
        }

        stream.Write(pcm, 0, pcm.Length);
        stream.Position = 0;
        return stream;
    }
}
