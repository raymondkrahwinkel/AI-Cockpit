using Cockpit.Core.Audio;

namespace Cockpit.Core.Tests.Audio;

public sealed class AudioDuckingPolicyTests
{
    [Fact]
    public void SpeechWindow_CapsRelativeVolumeOnce_AndPreservesOperatorChanges()
    {
        Assert.Equal(0.4, AudioDuckingPolicy.TargetVolume(0.9, 0.8, 0.5), 6);
        Assert.Equal(0.3, AudioDuckingPolicy.TargetVolume(0.3, 0.8, 0.5), 6);
        Assert.Equal(0, AudioDuckingPolicy.TargetVolume(0.9, 0.8, 0), 6);
        Assert.True(AudioDuckingPolicy.ShouldRestore(0.4, 0.4));
        Assert.False(AudioDuckingPolicy.ShouldRestore(0.2, 0.4));
        Assert.False(AudioDuckingPolicy.ShouldRestore(0.6, 0.4));
        Assert.False(AudioDuckingPolicy.ShouldStart(enabled: false, alreadyActive: false));
        Assert.True(AudioDuckingPolicy.ShouldStart(enabled: true, alreadyActive: false));
        Assert.False(AudioDuckingPolicy.ShouldStart(enabled: true, alreadyActive: true));

        var stopped = new DateTimeOffset(2026, 11, 5, 20, 0, 0, TimeSpan.Zero);
        Assert.False(AudioDuckingPolicy.ReleaseDue(stopped.AddMilliseconds(499), stopped));
        Assert.True(AudioDuckingPolicy.ReleaseDue(stopped.AddMilliseconds(500), stopped));
        Assert.False(AudioDuckingPolicy.ReleaseDue(stopped.AddSeconds(1), null));
    }
}
