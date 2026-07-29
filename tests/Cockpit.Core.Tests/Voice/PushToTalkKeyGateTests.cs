using Avalonia.Input;
using Cockpit.App.Services;

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
        Assert.True(PushToTalkKeyGate.ShouldHandleLocally(Key.F9, "F9", globalPushToTalkEnabled: false, openMicListening: false));
    }

    [Fact]
    public void ShouldHandleLocally_MatchingKey_GlobalEnabled_ReturnsFalse()
    {
        Assert.False(PushToTalkKeyGate.ShouldHandleLocally(Key.F9, "F9", globalPushToTalkEnabled: true, openMicListening: false));
    }

    [Fact]
    public void ShouldHandleLocally_MatchingKey_OpenMicListening_ReturnsFalse()
    {
        Assert.False(PushToTalkKeyGate.ShouldHandleLocally(Key.F9, "F9", globalPushToTalkEnabled: false, openMicListening: true));
    }

    [Fact]
    public void ShouldHandleLocally_NonMatchingKey_ReturnsFalse()
    {
        Assert.False(PushToTalkKeyGate.ShouldHandleLocally(Key.F8, "F9", globalPushToTalkEnabled: false, openMicListening: false));
    }

    [Fact]
    public void ShouldHandleLocally_UnparsableConfiguredKeyName_ReturnsFalse()
    {
        Assert.False(PushToTalkKeyGate.ShouldHandleLocally(Key.F9, "not-a-key", globalPushToTalkEnabled: false, openMicListening: false));
    }
}
