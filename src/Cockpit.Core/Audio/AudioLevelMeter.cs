namespace Cockpit.Core.Audio;

/// <summary>
/// Pure loudness measurement for the voice overlay's live meter: turns a raw signed-16-bit
/// little-endian PCM frame into a 0..1 level. Uses a decibel (dBFS) scale rather than raw RMS because
/// speech sits far below full scale — a linear RMS meter leaves the bars hugging the floor, while
/// mapping a soft-speech-to-near-clip dB window onto 0..1 makes ordinary talking fill most of the meter.
/// The read-only mirror of <see cref="PcmSampleConverter"/> — bytes in, one level out, no allocation.
/// </summary>
public static class AudioLevelMeter
{
    /// <summary>RMS at/below this dBFS reads as silence (0). Roughly the level of a quiet room.</summary>
    public const double FloorDb = -55.0;

    /// <summary>RMS at/above this dBFS reads as full (1). Just below clipping, so normal speech peaks near the top.</summary>
    public const double CeilingDb = -12.0;

    public static double NormalizedRms(ReadOnlySpan<byte> pcmS16, double floorDb = FloorDb, double ceilingDb = CeilingDb)
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
        if (rms <= 0)
        {
            return 0;
        }

        var dbfs = 20.0 * Math.Log10(rms);
        return Math.Clamp((dbfs - floorDb) / (ceilingDb - floorDb), 0, 1);
    }
}
