using Cockpit.Core.Voice;

namespace Cockpit.Core.Tests.Voice;

/// <summary>One start per physical hold, even under OS key-repeat, then a fresh hold after release.</summary>
public class PushToTalkHoldGuardTests
{
    [Fact]
    public void TryBeginHold_FirstCall_Succeeds()
    {
        var guard = new PushToTalkHoldGuard();

        Assert.True(guard.TryBeginHold());
    }

    [Fact]
    public void TryBeginHold_RepeatedWhileHeld_OnlyTheFirstCallSucceeds()
    {
        var guard = new PushToTalkHoldGuard();

        Assert.True(guard.TryBeginHold());
        Assert.False(guard.TryBeginHold());
        Assert.False(guard.TryBeginHold());
    }

    [Fact]
    public void TryBeginHold_AfterRelease_SucceedsAgain()
    {
        var guard = new PushToTalkHoldGuard();
        guard.TryBeginHold();

        guard.Release();

        Assert.True(guard.TryBeginHold());
    }
}
