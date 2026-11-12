namespace Dictum.Audio;

/// <summary>
/// Lightweight signal statistics used to reject a broken input. A disconnected
/// or floating microphone jack produces loud mains hum: very high RMS with a
/// near-zero zero-crossing rate. Sending that to Whisper only yields
/// hallucinations, so the app detects it and skips the take.
/// </summary>
public static class AudioStats
{
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
}
