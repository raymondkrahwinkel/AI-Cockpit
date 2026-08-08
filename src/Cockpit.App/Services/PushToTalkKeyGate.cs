using Avalonia.Input;

namespace Cockpit.App.Services;

// Whether a local per-view push-to-talk KeyDown/KeyUp should be handled. Extracted out of
// `SessionView`/`TtyView`'s code-behind (which used to duplicate the same key-name
// match) so the decision is unit-testable without an Avalonia UI thread. One thing makes the local
// handler stand down: global push-to-talk, since `VoicePushToTalkCoordinator` then already routes
// the hotkey to the selected session and handling it here too would fire the hold twice.
//
// Open-mic listening used to stand it down as well. AC-627 dropped that: the hold wins over open-mic now, and
// what open-mic does about it — pause, and throw away the sentence it was half-way through — belongs to
// `SessionPanelViewModel.BeginVoiceHold`, which both this route and the global one call. A copy of
// the rule here is how the two doors come apart.
public static class PushToTalkKeyGate
{
    public static bool ShouldHandleLocally(
        Key pressedKey, string configuredKeyName, bool globalPushToTalkEnabled) =>
        !globalPushToTalkEnabled
        && Enum.TryParse<Key>(configuredKeyName, ignoreCase: true, out var configuredKey)
        && pressedKey == configuredKey;
}
