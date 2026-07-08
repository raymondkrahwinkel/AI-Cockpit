using Cockpit.Core.Audio;
using FluentAssertions;

namespace Cockpit.Core.Tests.Audio;

/// <summary>
/// <see cref="PcmSampleConverter.ToInt16Bytes"/> — the float32-to-int16 conversion TTS playback needs
/// (the mirror of <c>VoicePushToTalkService._ToFloatSamples</c>, int16-to-float for STT capture).
/// </summary>
public class PcmSampleConverterTests
{
    [Fact]
    public void ToInt16Bytes_Silence_ProducesZeroBytes()
    {
        var bytes = PcmSampleConverter.ToInt16Bytes([0f, 0f]);

        bytes.Should().Equal(0, 0, 0, 0);
    }

    [Fact]
    public void ToInt16Bytes_FullScalePositive_ProducesMaxShort_LittleEndian()
    {
        var bytes = PcmSampleConverter.ToInt16Bytes([1f]);

        // short.MaxValue (32767) little-endian: low byte 0xFF, high byte 0x7F.
        bytes.Should().Equal(0xFF, 0x7F);
    }

    [Fact]
    public void ToInt16Bytes_FullScaleNegative_ProducesMinShort_LittleEndian()
    {
        var bytes = PcmSampleConverter.ToInt16Bytes([-1f]);

        // (short)(-1f * 32767) == -32767 == 0x8001 little-endian: low byte 0x01, high byte 0x80.
        bytes.Should().Equal(0x01, 0x80);
    }

    [Fact]
    public void ToInt16Bytes_OutOfRangeSamples_AreClampedBeforeConversion()
    {
        var bytes = PcmSampleConverter.ToInt16Bytes([2f, -2f]);

        bytes.Should().Equal(0xFF, 0x7F, 0x01, 0x80);
    }

    [Fact]
    public void ToInt16Bytes_MultipleSamples_ProducesTwoBytesPerSampleInOrder()
    {
        var bytes = PcmSampleConverter.ToInt16Bytes([0f, 1f, -1f]);

        bytes.Should().HaveCount(6);
        bytes[0..2].Should().Equal(0, 0);
        bytes[2..4].Should().Equal(0xFF, 0x7F);
        bytes[4..6].Should().Equal(0x01, 0x80);
    }
}
