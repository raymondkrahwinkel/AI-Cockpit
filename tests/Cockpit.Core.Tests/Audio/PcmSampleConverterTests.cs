using Cockpit.Core.Audio;

namespace Cockpit.Core.Tests.Audio;

/// <summary>
/// <see cref="PcmSampleConverter.ToInt16Bytes"/> — the float32-to-int16 conversion TTS playback needs
/// (the mirror of <c>VoicePushToTalkService._ToFloatSamples</c>, int16-to-float for STT capture).
/// </summary>
public class PcmSampleConverterTests
{
    [Fact]
    public void ToInt16Bytes_OutOfRangeSamples_AreClampedBeforeConversion()
    {
        var bytes = PcmSampleConverter.ToInt16Bytes([2f, -2f]);

        Assert.Equal(new byte[] { 0xFF, 0x7F, 0x01, 0x80 }, bytes);
    }

    /// <summary>
    /// One run spells out the whole conversion: two bytes per sample, in order, with silence, full-scale positive
    /// (32767 == 0xFF 0x7F little-endian) and full-scale negative (-32767 == 0x01 0x80) each landing where they
    /// should — the byte order is the half that is easy to ship reversed and impossible to hear as anything but noise.
    /// </summary>
    [Fact]
    public void ToInt16Bytes_MultipleSamples_ProducesTwoBytesPerSampleInOrder()
    {
        var bytes = PcmSampleConverter.ToInt16Bytes([0f, 1f, -1f]);

        Assert.Equal(6, System.Linq.Enumerable.Count(bytes));
        Assert.Equal(new byte[] { 0, 0 }, bytes[0..2]);
        Assert.Equal(new byte[] { 0xFF, 0x7F }, bytes[2..4]);
        Assert.Equal(new byte[] { 0x01, 0x80 }, bytes[4..6]);
    }
}
