using System.IO;

namespace Dictum.Audio;

/// <summary>
/// A WAV file that stands in for the microphone. Lets the whole capture path
/// (buffering, hum guard, VAD, normalization) be driven deterministically when
/// no working input device exists. Only the format Whisper expects is accepted:
/// 16 kHz, mono, 16-bit PCM.
/// </summary>
public sealed class FixtureSource
{
    public const int SampleRate = 16000;
    public const int BytesPerSample = 2;

    /// <summary>10 ms of 16 kHz mono 16-bit audio.</summary>
    public const int ChunkBytes = 320;

    private readonly byte[] _pcm;

    private FixtureSource(string path, byte[] pcm)
    {
        Path = path;
        _pcm = pcm;
    }

    public string Path { get; }

    public int Length => _pcm.Length;

    public int DurationMs => _pcm.Length / BytesPerSample * 1000 / SampleRate;

    public int ChunkCount => (_pcm.Length + ChunkBytes - 1) / ChunkBytes;

    /// <summary>Returns the chunk at <paramref name="index"/>, empty past the end.</summary>
    public byte[] Chunk(int index)
    {
        var start = index * ChunkBytes;
        if (index < 0 || start >= _pcm.Length)
        {
            return Array.Empty<byte>();
        }

        var count = Math.Min(ChunkBytes, _pcm.Length - start);
        var chunk = new byte[count];
        Array.Copy(_pcm, start, chunk, 0, count);
        return chunk;
    }

    public byte[] ToArray() => (byte[])_pcm.Clone();

    public static (FixtureSource? Source, string? Error) TryLoad(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = new BinaryReader(stream);

            static string FourCC(BinaryReader r) => new(r.ReadChars(4));

            if (FourCC(reader) != "RIFF" || reader.ReadInt32() != bytes.Length - 8 || FourCC(reader) != "WAVE")
            {
                return (null, "not a RIFF/WAVE file");
            }

            short channels = 0, bits = 0;
            int rate = 0, format = 0;
            byte[]? data = null;

            while (stream.Position + 8 <= stream.Length)
            {
                var id = FourCC(reader);
                var size = reader.ReadInt32();
                if (size < 0 || stream.Position + size > stream.Length)
                {
                    return (null, $"truncated '{id}' chunk");
                }

                var start = stream.Position;
                if (id == "fmt " && size >= 16)
                {
                    format = reader.ReadUInt16();
                    channels = reader.ReadInt16();
                    rate = reader.ReadInt32();
                    _ = reader.ReadInt32(); // byte rate
                    _ = reader.ReadInt16(); // block align
                    bits = reader.ReadInt16();
                }
                else if (id == "data")
                {
                    data = reader.ReadBytes(size);
                }

                stream.Position = start + size + (size & 1); // chunks are word-aligned
            }

            if (format != 1 || channels != 1 || rate != SampleRate || bits != 16)
            {
                return (null, $"need 16 kHz mono 16-bit PCM, got format={format} channels={channels} rate={rate} bits={bits}");
            }

            if (data is null || data.Length < BytesPerSample)
            {
                return (null, "no audio data");
            }

            if (data.Length % BytesPerSample != 0)
            {
                data = data[..(data.Length - data.Length % BytesPerSample)];
            }

            return (new FixtureSource(path, data), null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }
}
