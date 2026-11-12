namespace Dictum.Audio;

/// <summary>
/// Lightweight signal statistics used to reject a broken input. A disconnected
/// or floating microphone jack produces loud mains hum: very high RMS with a
/// near-zero zero-crossing rate and a flat 20 ms envelope. Sending that to
/// Whisper only yields hallucinations, so the app detects it and skips the take.
/// All metrics are computed on the DC-removed signal: a biased-but-broken
/// input can carry a large constant offset that mimics loudness.
/// </summary>
public static class AudioStats
{
    public sealed record Metrics(double Rms, double Zcr, double Flatness, double LowBand, double HighBand);

    public static double Rms(byte[] pcm)
    {
        var samples = pcm.Length / 2;
        if (samples == 0)
        {
            return 0;
        }

        double sum = 0;

        for (var i = 0; i < samples; i++)
        {
            var value = BitConverter.ToInt16(pcm, i * 2);
            sum += (double)value * value;
        }

        return Math.Sqrt(sum / samples);
    }

    public static double ZeroCrossingRate(byte[] pcm)
    {
        var samples = pcm.Length / 2;
        if (samples < 2)
        {
            return 0;
        }

        var crossings = 0;
        var previous = BitConverter.ToInt16(pcm, 0);

        for (var i = 1; i < samples; i++)
        {
            var value = BitConverter.ToInt16(pcm, i * 2);

            if ((value >= 0 && previous < 0) || (value < 0 && previous >= 0))
            {
                crossings++;
            }

            previous = value;
        }

        return (double)crossings / samples;
    }

    /// <summary>
    /// RMS, zero-crossing rate and envelope flatness (max/min of 20 ms window
    /// RMS) computed after removing the DC offset. Speech recorded from any
    /// working microphone has pauses and syllabic dips, so its envelope
    /// varies by more than a factor of a few; mains hum is constant from the
    /// first sample, which keeps the ratio near 1.
    /// </summary>
    public static Metrics Analyze(byte[] pcm)
    {
        var samples = pcm.Length / 2;
        if (samples == 0)
        {
            return new Metrics(0, 0, 1, 0, 0);
        }

        double dc = 0;
        for (var i = 0; i < samples; i++)
        {
            dc += BitConverter.ToInt16(pcm, i * 2);
        }
        dc /= samples;

        double sum = 0;
        var crossings = 0;
        var previous = BitConverter.ToInt16(pcm, 0) - dc;

        const int window = 320; // 20 ms at 16 kHz mono
        var windowRms = new List<double>(samples / window + 1);
        var windowAcc = 0.0;
        var windowCount = 0;

        for (var i = 0; i < samples; i++)
        {
            var value = BitConverter.ToInt16(pcm, i * 2) - dc;
            sum += value * value;
            windowAcc += value * value;
            windowCount++;

            if (i > 0 && ((value >= 0 && previous < 0) || (value < 0 && previous >= 0)))
            {
                crossings++;
            }
            previous = value;

            if (windowCount == window)
            {
                windowRms.Add(Math.Sqrt(windowAcc / window));
                windowAcc = 0;
                windowCount = 0;
            }
        }

        if (windowCount > 0)
        {
            windowRms.Add(Math.Sqrt(windowAcc / windowCount));
        }

        var flatness = 1.0;
        if (windowRms.Count >= 3)
        {
            var min = windowRms.Min();
            var max = windowRms.Max();
            flatness = min > 0 ? max / min : max > 0 ? double.MaxValue : 1;
        }

        return new Metrics(
            Math.Sqrt(sum / samples),
            (double)crossings / samples,
            flatness,
            BandEnergy(pcm, samples, MainsBand),
            BandEnergy(pcm, samples, SpeechBand));
    }

    private static readonly int[] MainsBand = { 50, 100, 150 };
    private static readonly int[] SpeechBand = { 800, 1200, 2000 };

    /// <summary>
    /// RMS amplitude at the given frequencies over the whole take (Goertzel).
    /// Mains hum lives at 50-150 Hz; human speech always carries 800-2000 Hz
    /// formants and fricative noise, even from a bass voice.
    /// </summary>
    private static double BandEnergy(byte[] pcm, int samples, int[] frequencies)
    {
        if (samples < 64)
        {
            return 0;
        }

        double total = 0;
        foreach (var freq in frequencies)
        {
            var w = 2 * Math.PI * freq / 16000;
            var coeff = 2 * Math.Cos(w);
            double s1 = 0, s2 = 0;

            for (var i = 0; i < samples; i++)
            {
                var s0 = BitConverter.ToInt16(pcm, i * 2) + coeff * s1 - s2;
                s2 = s1;
                s1 = s0;
            }

            total += Math.Sqrt(Math.Max(0, s1 * s1 + s2 * s2 - coeff * s1 * s2)) / samples;
        }

        return total;
    }

    /// <summary>
    /// A floating jack signature: loud, almost no zero crossings, and either
    /// a flat envelope or energy confined to the mains band with an empty
    /// speech band. An intermittent contact produces hum bursts that defeat
    /// the envelope check, so the spectral test catches them too. Observed
    /// band ratios: mains hum 0.002-0.004, any real voice 0.03+, so the
    /// 0.02 threshold sits between the classes with margin on both sides.
    /// </summary>
    public static bool LooksLikeHum(Metrics metrics)
        => metrics.Rms > 3000
           && metrics.Zcr < 0.02
           && (metrics.Flatness < 2.5 || metrics.HighBand < 0.02 * metrics.LowBand);
}
