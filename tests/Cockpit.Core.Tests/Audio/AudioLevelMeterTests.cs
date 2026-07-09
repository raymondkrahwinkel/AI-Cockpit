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
    public void NormalizedRms_OrdinarySpeechLevel_FillsMostOfTheMeter()
    {
        // A constant-amplitude frame has RMS == that amplitude; 0.1 is -20 dBFS, which on the default
        // -55..-12 dB window maps to ~0.81 — well up the meter rather than hugging the floor.
        var speech = ConstantFrame(0.1f, sampleCount: 128);

        AudioLevelMeter.NormalizedRms(speech).Should().BeApproximately(0.81, 0.02);
    }

    [Fact]
    public void NormalizedRms_BelowTheNoiseFloor_ReadsAsZero()
    {
        var veryQuiet = ConstantFrame(0.001f, sampleCount: 128);

        AudioLevelMeter.NormalizedRms(veryQuiet).Should().Be(0);
    }

    [Fact]
    public void NormalizedRms_LouderInput_ReadsHigherThanQuieter()
    {
        var quiet = ConstantFrame(0.02f, sampleCount: 128);
        var loud = ConstantFrame(0.2f, sampleCount: 128);

        AudioLevelMeter.NormalizedRms(loud).Should().BeGreaterThan(AudioLevelMeter.NormalizedRms(quiet));
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
