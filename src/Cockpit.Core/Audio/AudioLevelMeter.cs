namespace Cockpit.Core.Audio;

/// <summary>
/// Pure loudness measurement for the voice overlay's live meter: turns a raw signed-16-bit
/// little-endian PCM frame into a 0..1 level (RMS with a gain so ordinary speech fills the bars rather
/// than hugging the floor). The read-only mirror of <see cref="PcmSampleConverter"/> — bytes in, one
/// level out, no allocation.
/// </summary>
public static class AudioLevelMeter
{
    /// <summary>Gain applied to the raw RMS so typical speech (RMS ~0.05-0.15) reaches most of the meter's range.</summary>
    public const double DefaultGain = 6.0;

    public static double NormalizedRms(ReadOnlySpan<byte> pcmS16, double gain = DefaultGain)
    {
        var sampleCount = pcmS16.Length / 2;
        if (sampleCount == 0)
        {
            return 0;
        }

        double sumSquares = 0;
        for (var i = 0; i < sampleCount; i++)
        {
            var s16 = (short)(pcmS16[i * 2] | (pcmS16[(i * 2) + 1] << 8));
            var sample = s16 / (double)short.MaxValue;
            sumSquares += sample * sample;
        }

        var rms = Math.Sqrt(sumSquares / sampleCount);
        return Math.Clamp(rms * gain, 0, 1);
    }
}
