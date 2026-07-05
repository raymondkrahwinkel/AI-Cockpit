using FluentAssertions;
using Cockpit.Core.Audio;

namespace Cockpit.Core.Tests.Audio;

public class AudioFormatTests
{
    [Fact]
    public void Constructor_NoArguments_UsesWhisperTargetDefaults()
    {
        var format = new AudioFormat();

        format.SampleRate.Should().Be(16000);
        format.Channels.Should().Be(1);
        format.BitsPerSample.Should().Be(16);
    }

    [Fact]
    public void Constructor_CustomValues_OverridesDefaults()
    {
        var format = new AudioFormat(SampleRate: 48000, Channels: 2, BitsPerSample: 24);

        format.SampleRate.Should().Be(48000);
        format.Channels.Should().Be(2);
        format.BitsPerSample.Should().Be(24);
    }
}
