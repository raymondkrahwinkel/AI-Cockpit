namespace Cockpit.Core.Abstractions.Hotkeys;

/// <summary>
/// The identifiers the cockpit's global hotkeys are registered under. Constants rather than literals because
/// on Linux these are the names a compositor files the operator's binding under — a typo on one side and the
/// key they bound belongs to a shortcut nothing listens for.
/// </summary>
public static class GlobalHotkeys
{
    /// <summary>Hold to dictate (#34). Named as it has been since it was the only one, so an existing binding survives.</summary>
    public const string PushToTalk = "cockpit_push_to_talk";

    /// <summary>Press to capture the screen into the selected session (AC-220).</summary>
    public const string Screenshot = "cockpit_screenshot";

    /// <summary>
    /// Hold to talk to the voice assistant (AC-543) — F10 by default, beside dictation's F9. Its own id and its
    /// own key on purpose: <see cref="PushToTalk"/> puts your words into the selected session and this one puts
    /// them to the assistant, and the damaging mistake is words landing in the wrong one. Global in nature
    /// because the assistant is who you speak to while the cockpit sits behind another window.
    /// </summary>
    public const string AssistantPushToTalk = "cockpit_assistant_push_to_talk";
}
