namespace Dictum.Audio;

/// <summary>
/// Peak normalisation before transcription. A quiet microphone is the most
/// common cause of recognition errors, and bringing the peak to about −3 dBFS
/// is cheaper and safer than switching models. Only boosts, never attenuates,
/// and caps the gain so the noise floor is not blown up.
/// </summary>
public static class AudioNormalizer
{
    public static int Peak(byte[] pcm)
    {
        var samples = pcm.Length / 2;
        var peak = 0;

        for (var i = 0; i < samples; i++)
        {
            var value = Math.Abs((int)BitConverter.ToInt16(pcm, i * 2));
            if (value > peak)
            {
                peak = value;
            }
        }

        return peak;
    }

    public static (byte[] Pcm, double GainDb) Normalize(
        byte[] pcm,
        double targetPeakDb = -3.0,
        double maxGainDb = 18.0)
    {
        var samples = pcm.Length / 2;
        if (samples == 0)
        {
            return (pcm, 0);
        }

        var peak = Peak(pcm);

        if (peak < 40)
        {
            return (pcm, 0);
        }

        var target = 32767 * Math.Pow(10, targetPeakDb / 20.0);
        var gain = target / peak;
        var maxGain = Math.Pow(10, maxGainDb / 20.0);

        if (gain > maxGain)
        {
            gain = maxGain;
        }

        if (gain <= 1.01)
        {
            return (pcm, 0);
        }

        var result = new byte[pcm.Length];

        for (var i = 0; i < samples; i++)
        {
            var value = (int)(BitConverter.ToInt16(pcm, i * 2) * gain);

            if (value > 32767)
            {
                value = 32767;
            }
            else if (value < -32768)
            {
                value = -32768;
            }

            result[i * 2] = (byte)(value & 0xFF);
            result[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
        }

        return (result, 20 * Math.Log10(gain));
    }
}
