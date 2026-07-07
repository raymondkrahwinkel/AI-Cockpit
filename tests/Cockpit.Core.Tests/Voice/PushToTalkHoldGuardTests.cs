using Cockpit.Core.Voice;
using FluentAssertions;

namespace Cockpit.Core.Tests.Voice;

/// <summary>One start per physical hold, even under OS key-repeat, then a fresh hold after release.</summary>
public class PushToTalkHoldGuardTests
{
    [Fact]
    public void TryBeginHold_FirstCall_Succeeds()
    {
        var guard = new PushToTalkHoldGuard();

        guard.TryBeginHold().Should().BeTrue();
    }

    [Fact]
    public void TryBeginHold_RepeatedWhileHeld_OnlyTheFirstCallSucceeds()
    {
        var guard = new PushToTalkHoldGuard();

        guard.TryBeginHold().Should().BeTrue();
        guard.TryBeginHold().Should().BeFalse();
        guard.TryBeginHold().Should().BeFalse();
    }

    [Fact]
    public void TryBeginHold_AfterRelease_SucceedsAgain()
    {
        var guard = new PushToTalkHoldGuard();
        guard.TryBeginHold();

        guard.Release();

        guard.TryBeginHold().Should().BeTrue();
    }
}
