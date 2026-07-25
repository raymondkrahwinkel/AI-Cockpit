namespace Cockpit.Core.Screenshots;

/// <summary>
/// Operator-configurable screenshot settings (AC-220), persisted under the <c>screenshots</c> section of
/// <c>cockpit.json</c> — the same store pattern as voice/notifications/layout.
/// </summary>
public sealed record ScreenshotSettings
{
    /// <summary>
    /// Whether the desktop-wide screenshot key is armed. Off by default, like global push-to-talk: a key that
    /// works while the cockpit has no focus is taken from every other application on the machine, and that is
    /// the operator's to grant rather than ours to assume. The in-app button works either way.
    /// </summary>
    public bool GlobalHotkeyEnabled { get; init; }

    /// <summary>
    /// Avalonia <c>Key</c> enum name for the screenshot hotkey. F8 sits next to push-to-talk's F9 and is
    /// unclaimed by the desktops the cockpit runs on; on Linux it is a request rather than a decision (see
    /// <c>IGlobalHotkeyService.TriggerDescriptionFor</c>).
    /// </summary>
    public string HotkeyKeyName { get; init; } = "F8";
}
