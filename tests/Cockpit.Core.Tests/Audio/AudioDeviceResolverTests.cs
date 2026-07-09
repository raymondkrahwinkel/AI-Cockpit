using Cockpit.Core.Audio;
using FluentAssertions;

namespace Cockpit.Core.Tests.Audio;

/// <summary>The pure name-to-device matching with system-default fallback behind the Options device pickers.</summary>
public class AudioDeviceResolverTests
{
    private static readonly string[] Devices = ["Built-in Microphone", "Yeti Stereo Microphone", "Webcam Mic"];

    [Fact]
    public void FindIndex_EmptyName_ReturnsSystemDefaultSentinel()
    {
        AudioDeviceResolver.FindIndex("", Devices).Should().Be(-1);
    }

    [Fact]
    public void FindIndex_NullName_ReturnsSystemDefaultSentinel()
    {
        AudioDeviceResolver.FindIndex(null, Devices).Should().Be(-1);
    }

    [Fact]
    public void FindIndex_KnownName_ReturnsItsIndex()
    {
        AudioDeviceResolver.FindIndex("Yeti Stereo Microphone", Devices).Should().Be(1);
    }

    [Fact]
    public void FindIndex_NameNoLongerPresent_FallsBackToSystemDefault()
    {
        AudioDeviceResolver.FindIndex("Unplugged Headset", Devices).Should().Be(-1);
    }

    [Fact]
    public void FindIndex_MatchIsCaseSensitive_SinceDeviceNamesAreExact()
    {
        AudioDeviceResolver.FindIndex("yeti stereo microphone", Devices).Should().Be(-1);
    }
}
