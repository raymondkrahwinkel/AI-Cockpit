namespace Cockpit.Core.Abstractions.Hotkeys;

// The identifiers the cockpit's global hotkeys are registered under. Constants rather than literals because
// on Linux these are the names a compositor files the operator's binding under — a typo on one side and the
// key they bound belongs to a shortcut nothing listens for.
public static class GlobalHotkeys
{
    // Hold to dictate (#34). Named as it has been since it was the only one, so an existing binding survives.
    public const string PushToTalk = "cockpit_push_to_talk";

    // Press to capture the screen into the selected session (AC-220).
    public const string Screenshot = "cockpit_screenshot";

    // Hold to talk to the voice assistant (AC-543) — F10 by default, beside dictation's F9. Own id/key
    // on purpose: PushToTalk feeds the selected session, this feeds the assistant, and the damaging
    // mistake is words landing in the wrong one.
    public const string AssistantPushToTalk = "cockpit_assistant_push_to_talk";
}
