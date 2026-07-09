using Cockpit.Core.Audio;
using FluentAssertions;

namespace Cockpit.Core.Tests.Audio;

/// <summary>The pure microphone-loudness measurement behind the voice overlay's live waveform (#34b).</summary>
public class AudioLevelMeterTests
{
    [Fact]
    public void NormalizedRms_Silence_IsZero()
    {
        var silence = ConstantFrame(0f, sampleCount: 64);

        AudioLevelMeter.NormalizedRms(silence).Should().Be(0);
    }

    [Fact]
    public void NormalizedRms_EmptyFrame_IsZero()
    {
        AudioLevelMeter.NormalizedRms(ReadOnlySpan<byte>.Empty).Should().Be(0);
    }

    [Fact]
    public void NormalizedRms_FullScale_ClampsToOne()
    {
        var loud = ConstantFrame(1f, sampleCount: 64);

        AudioLevelMeter.NormalizedRms(loud).Should().Be(1);
    }

    [Fact]
    public void NormalizedRms_AppliesGain_ToRaiseQuietSpeechOffTheFloor()
    {
        // A constant-amplitude frame has RMS == that amplitude, so gain 4 turns 0.1 into 0.4.
        var quiet = ConstantFrame(0.1f, sampleCount: 128);

        AudioLevelMeter.NormalizedRms(quiet, gain: 4.0).Should().BeApproximately(0.4, 0.01);
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
