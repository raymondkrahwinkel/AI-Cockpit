using Avalonia.Input;

namespace Cockpit.App.Services;

// Whether a local per-view push-to-talk KeyDown/KeyUp should be handled. Extracted out of
// `SessionView`/`TtyView`'s code-behind (which used to duplicate the same key-name
// match) so the decision is unit-testable without an Avalonia UI thread. Two things make the local
// handler stand down: global push-to-talk (then `VoicePushToTalkCoordinator` already routes the
// hotkey to the selected session, so handling it here too would fire the hold twice), and open-mic
// dictation (already capturing and transcribing continuously, so a hold on top transcribes the same
// speech a second time).
public static class PushToTalkKeyGate
{
    public static bool ShouldHandleLocally(
        Key pressedKey, string configuredKeyName, bool globalPushToTalkEnabled, bool openMicListening) =>
        !globalPushToTalkEnabled
        && !openMicListening
        && Enum.TryParse<Key>(configuredKeyName, ignoreCase: true, out var configuredKey)
        && pressedKey == configuredKey;
}
