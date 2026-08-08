using Avalonia.Input;
using Cockpit.App.Services;

namespace Cockpit.Core.Tests.Voice;

/// <summary>
/// The local per-view push-to-talk gate (#34): a per-view KeyDown/KeyUp must no-op once global push-to-talk is
/// active (<c>VoicePushToTalkCoordinator</c> already routes that hold to the selected session), or the same hold
/// would fire twice.
/// </summary>
/// <remarks>
/// Open-mic listening used to stand it down too, and a case here asserted that. AC-627 reversed the rule — the
/// hold wins over open-mic now — and the reversal is deliberately not a second condition here: what open-mic does
/// about a hold lives in <c>SessionPanelViewModel.BeginVoiceHold</c>, which is the one method both this route and
/// the global one call. See <c>VoiceInjectionTests</c> for the in-window half of criterion 6.
/// </remarks>
public class PushToTalkKeyGateTests
{
    [Fact]
    public void ShouldHandleLocally_MatchingKey_GlobalDisabled_ReturnsTrue()
    {
        Assert.True(PushToTalkKeyGate.ShouldHandleLocally(Key.F9, "F9", globalPushToTalkEnabled: false));
    }

    [Fact]
    public void ShouldHandleLocally_MatchingKey_GlobalEnabled_ReturnsFalse()
    {
        Assert.False(PushToTalkKeyGate.ShouldHandleLocally(Key.F9, "F9", globalPushToTalkEnabled: true));
    }

    [Fact]
    public void ShouldHandleLocally_NonMatchingKey_ReturnsFalse()
    {
        Assert.False(PushToTalkKeyGate.ShouldHandleLocally(Key.F8, "F9", globalPushToTalkEnabled: false));
    }

    [Fact]
    public void ShouldHandleLocally_UnparsableConfiguredKeyName_ReturnsFalse()
    {
        Assert.False(PushToTalkKeyGate.ShouldHandleLocally(Key.F9, "not-a-key", globalPushToTalkEnabled: false));
    }
}
