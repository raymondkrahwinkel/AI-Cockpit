using Cockpit.Core.Audio;

namespace Cockpit.Core.Tests.Audio;

/// <summary>The pure name-to-device matching with system-default fallback behind the Options device pickers.</summary>
public class AudioDeviceResolverTests
{
    private static readonly string[] Devices = ["Built-in Microphone", "Yeti Stereo Microphone", "Webcam Mic"];

    /// <summary>
    /// A stored name is matched exactly — device names are exact, so a looser match would pick a device the
    /// operator did not choose — and anything that does not match falls back to the system default sentinel.
    /// </summary>
    [Theory]
    [InlineData(null, -1)]
    [InlineData("Yeti Stereo Microphone", 1)]
    [InlineData("yeti stereo microphone", -1)]
    public void FindIndex_MatchesExactly_OrFallsBackToTheSystemDefaultSentinel(string? name, int expected)
    {
        Assert.Equal(expected, AudioDeviceResolver.FindIndex(name, Devices));
    }
}
