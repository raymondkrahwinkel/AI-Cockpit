using Avalonia.Input;

namespace Cockpit.App.Services;

/// <summary>
/// Whether a local per-view push-to-talk KeyDown/KeyUp should be handled. Extracted out of
/// <c>SessionView</c>/<c>ClaudeTtyView</c>'s code-behind (which used to duplicate the same key-name
/// match) so the decision is unit-testable without an Avalonia UI thread. When global push-to-talk is
/// on, <c>VoicePushToTalkCoordinator</c> already routes the hotkey to the selected session, so the local
/// handler must no-op — otherwise the same hold would fire twice.
/// </summary>
public static class PushToTalkKeyGate
{
    public static bool ShouldHandleLocally(Key pressedKey, string configuredKeyName, bool globalPushToTalkEnabled) =>
        !globalPushToTalkEnabled
        && Enum.TryParse<Key>(configuredKeyName, ignoreCase: true, out var configuredKey)
        && pressedKey == configuredKey;
}
