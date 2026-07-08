using Avalonia.Input;
using Cockpit.App.Services;
using FluentAssertions;

namespace Cockpit.Core.Tests.Voice;

/// <summary>
/// The local per-view push-to-talk gate (#34): a per-view KeyDown/KeyUp must no-op once global
/// push-to-talk is active, since <c>VoicePushToTalkCoordinator</c> already routes that hold to the
/// selected session — without this gate the same hold would fire twice (once locally, once globally).
/// </summary>
public class PushToTalkKeyGateTests
{
    [Fact]
    public void ShouldHandleLocally_MatchingKey_GlobalDisabled_ReturnsTrue()
    {
        PushToTalkKeyGate.ShouldHandleLocally(Key.F9, "F9", globalPushToTalkEnabled: false).Should().BeTrue();
    }

    [Fact]
    public void ShouldHandleLocally_MatchingKey_GlobalEnabled_ReturnsFalse()
    {
        PushToTalkKeyGate.ShouldHandleLocally(Key.F9, "F9", globalPushToTalkEnabled: true).Should().BeFalse();
    }

    [Fact]
    public void ShouldHandleLocally_NonMatchingKey_ReturnsFalse()
    {
        PushToTalkKeyGate.ShouldHandleLocally(Key.F8, "F9", globalPushToTalkEnabled: false).Should().BeFalse();
    }

    [Fact]
    public void ShouldHandleLocally_UnparsableConfiguredKeyName_ReturnsFalse()
    {
        PushToTalkKeyGate.ShouldHandleLocally(Key.F9, "not-a-key", globalPushToTalkEnabled: false).Should().BeFalse();
    }
}
