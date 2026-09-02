using Cockpit.Core.Audio;

namespace Cockpit.Core.Tests.Audio;

/// <summary>The pure microphone-loudness measurement behind the voice overlay's live waveform (#34b).</summary>
public class AudioLevelMeterTests
{
    /// <summary>
    /// A constant-amplitude frame has RMS == that amplitude, so these are the four points that fix the whole
    /// -55..-12 dB window: the floor and anything under it read as nothing, full scale clamps rather than
    /// overflowing, and an ordinary -20 dBFS speech level sits well up the meter instead of hugging the floor.
    /// </summary>
    [Theory]
    [InlineData(0f, 0.0)]
    [InlineData(0.001f, 0.0)]
    [InlineData(0.1f, 0.81)]
    [InlineData(1f, 1.0)]
    public void NormalizedRms_MapsAmplitudeOntoTheMeter(float amplitude, double expected)
    {
        Assert.Equal(expected, AudioLevelMeter.NormalizedRms(ConstantFrame(amplitude, sampleCount: 128)), 0.02);
    }

    [Fact]
    public void NormalizedRms_EmptyFrame_IsZero()
    {
        Assert.Equal(0, AudioLevelMeter.NormalizedRms(ReadOnlySpan<byte>.Empty));
    }

    private static byte[] ConstantFrame(float amplitude, int sampleCount)
    {
        var s16 = (short)(amplitude * short.MaxValue);
        var bytes = new byte[sampleCount * 2];
        for (var i = 0; i < sampleCount; i++)
        {
            bytes[i * 2] = (byte)(s16 & 0xFF);
            bytes[(i * 2) + 1] = (byte)((s16 >> 8) & 0xFF);
        }

        return bytes;
    }
}
