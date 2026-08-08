using Avalonia.Input;

namespace Cockpit.App.Services;

// Whether a local per-view push-to-talk KeyDown/KeyUp should be handled — extracted out of the views' code-behind
// so the decision is testable without a UI thread. False while global push-to-talk is on, or the coordinator and
// this handler each fire the same hold; open-mic stands it down no longer (AC-627).
public static class PushToTalkKeyGate
{
    public static bool ShouldHandleLocally(
        Key pressedKey, string configuredKeyName, bool globalPushToTalkEnabled) =>
        !globalPushToTalkEnabled
        && Enum.TryParse<Key>(configuredKeyName, ignoreCase: true, out var configuredKey)
        && pressedKey == configuredKey;
}
