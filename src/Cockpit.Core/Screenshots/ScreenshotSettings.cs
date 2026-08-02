using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.Core.Screenshots;

// Operator-configurable screenshot settings (AC-220), persisted under the `screenshots` section of
// `cockpit.json` — the same store pattern as voice/notifications/layout.
public sealed record ScreenshotSettings
{
    // Whether the desktop-wide screenshot key is armed. Off by default, like global push-to-talk: a key that
    // works while the cockpit has no focus is taken from every other application on the machine, and that is
    // the operator's to grant rather than ours to assume. The in-app button works either way.
    public bool GlobalHotkeyEnabled { get; init; }

    // Avalonia `Key` enum name for the screenshot hotkey. F8 sits next to push-to-talk's F9 and is
    // unclaimed by the desktops the cockpit runs on; on Linux it is a request rather than a decision (see
    // `IGlobalHotkeyService.TriggerDescriptionFor`).
    public string HotkeyKeyName { get; init; } = "F8";

    // The region marked out last time, in the captured image's own pixels, so the same panel does not have to
    // be dragged out again every capture (AC-329). Null until one has been taken, and dropped rather than
    // clamped when the desktop has since changed shape and it no longer fits.
    public CaptureRect? LastRegion { get; init; }

    // Whether confirming a selection opens a preview first instead of injecting straight into the session
    // (AC-566). Off by default — Raymond's own argument: not everyone wants an extra window between a drag and
    // a screenshot landing.
    public bool PreviewEnabled { get; init; }
}
