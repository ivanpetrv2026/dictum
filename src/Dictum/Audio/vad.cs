namespace Dictum.Audio;

/// <summary>
/// Energy-based voice activity detection: trims leading and trailing silence
/// and reports whether anything was said at all. Whisper hallucinates on pure
/// silence ("Продолжение следует..."), so a recording with no voiced frames is
/// dropped before it reaches the model.
/// </summary>
public static class Vad
{
    private const int FrameMs = 30;

    public static (int StartSample, int EndSample) FindSpeech(byte[] pcm, int sampleRate = 16000)
    {
        var samples = pcm.Length / 2;
        if (samples == 0)
        {
            return (0, 0);
        }

        var frameSize = sampleRate * FrameMs / 1000;
        var frames = samples / frameSize;
        if (frames == 0)
        {
            return (0, samples);
        }

        var energies = new double[frames];

        for (var frame = 0; frame < frames; frame++)
        {
            double sum = 0;

            for (var i = 0; i < frameSize; i++)
            {
                var index = (frame * frameSize + i) * 2;
                var sample = (short)(pcm[index] | (pcm[index + 1] << 8));
                sum += (double)sample * sample;
            }

            energies[frame] = Math.Sqrt(sum / frameSize);
        }

        var sorted = (double[])energies.Clone();
        Array.Sort(sorted);

        var noise = sorted[Math.Max(0, (int)(sorted.Length * 0.2))];
        var peak = sorted[^1];
        var threshold = Math.Max(Math.Max(noise * 2.5, peak * 0.08), 120);

        var first = -1;
        var last = -1;

        for (var frame = 0; frame < frames; frame++)
        {
            if (energies[frame] < threshold)
            {
                continue;
            }

            if (first < 0)
            {
                first = frame;
            }

            last = frame;
        }

        if (first < 0)
        {
            return (0, 0);
        }

        var start = Math.Max(0, (first - 1) * frameSize);
        var end = Math.Min(samples, (last + 2) * frameSize);

        return (start, end);
    }
}
