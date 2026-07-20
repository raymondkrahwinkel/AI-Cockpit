using Avalonia.Input;
using Cockpit.App.Services;
using FluentAssertions;

namespace Cockpit.Core.Tests.Voice;

/// <summary>
/// The local per-view push-to-talk gate (#34): a per-view KeyDown/KeyUp must no-op once global
/// push-to-talk is active (<c>VoicePushToTalkCoordinator</c> already routes that hold to the selected
/// session) or open-mic dictation is listening (already capturing continuously) — without the gate the same
/// speech would be transcribed twice.
/// </summary>
public class PushToTalkKeyGateTests
{
    [Fact]
    public void ShouldHandleLocally_MatchingKey_GlobalDisabled_ReturnsTrue()
    {
        PushToTalkKeyGate.ShouldHandleLocally(Key.F9, "F9", globalPushToTalkEnabled: false, openMicListening: false).Should().BeTrue();
    }

    [Fact]
    public void ShouldHandleLocally_MatchingKey_GlobalEnabled_ReturnsFalse()
    {
        PushToTalkKeyGate.ShouldHandleLocally(Key.F9, "F9", globalPushToTalkEnabled: true, openMicListening: false).Should().BeFalse();
    }

    [Fact]
    public void ShouldHandleLocally_MatchingKey_OpenMicListening_ReturnsFalse()
    {
        PushToTalkKeyGate.ShouldHandleLocally(Key.F9, "F9", globalPushToTalkEnabled: false, openMicListening: true).Should().BeFalse();
    }

    [Fact]
    public void ShouldHandleLocally_NonMatchingKey_ReturnsFalse()
    {
        PushToTalkKeyGate.ShouldHandleLocally(Key.F8, "F9", globalPushToTalkEnabled: false, openMicListening: false).Should().BeFalse();
    }

    [Fact]
    public void ShouldHandleLocally_UnparsableConfiguredKeyName_ReturnsFalse()
    {
        PushToTalkKeyGate.ShouldHandleLocally(Key.F9, "not-a-key", globalPushToTalkEnabled: false, openMicListening: false).Should().BeFalse();
    }
}
