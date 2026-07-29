using Cockpit.Core.Audio;

namespace Cockpit.Core.Tests.Audio;

public class AudioFormatTests
{
    [Fact]
    public void Constructor_NoArguments_UsesWhisperTargetDefaults()
    {
        var format = new AudioFormat();

        Assert.Equal(16000, format.SampleRate);
        Assert.Equal(1, format.Channels);
        Assert.Equal(16, format.BitsPerSample);
    }

    [Fact]
    public void Constructor_CustomValues_OverridesDefaults()
    {
        var format = new AudioFormat(SampleRate: 48000, Channels: 2, BitsPerSample: 24);

        Assert.Equal(48000, format.SampleRate);
        Assert.Equal(2, format.Channels);
        Assert.Equal(24, format.BitsPerSample);
    }
}
