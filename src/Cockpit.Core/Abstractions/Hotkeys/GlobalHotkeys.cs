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
}
