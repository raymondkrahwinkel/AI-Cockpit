using Cockpit.Core.Audio;

namespace Cockpit.Core.Tests.Audio;

/// <summary>The pure name-to-device matching with system-default fallback behind the Options device pickers.</summary>
public class AudioDeviceResolverTests
{
    private static readonly string[] Devices = ["Built-in Microphone", "Yeti Stereo Microphone", "Webcam Mic"];

    [Fact]
    public void FindIndex_NullName_ReturnsSystemDefaultSentinel()
    {
        Assert.Equal(-1, AudioDeviceResolver.FindIndex(null, Devices));
    }

    [Fact]
    public void FindIndex_KnownName_ReturnsItsIndex()
    {
        Assert.Equal(1, AudioDeviceResolver.FindIndex("Yeti Stereo Microphone", Devices));
    }

    [Fact]
    public void FindIndex_MatchIsCaseSensitive_SinceDeviceNamesAreExact()
    {
        Assert.Equal(-1, AudioDeviceResolver.FindIndex("yeti stereo microphone", Devices));
    }
}
