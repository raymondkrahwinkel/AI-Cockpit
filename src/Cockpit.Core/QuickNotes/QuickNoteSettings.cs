namespace Cockpit.Core.QuickNotes;

// The quick-note key (AC-492), persisted under `quickNotes` in `cockpit.json` — the same store pattern as
// screenshots. Only the desktop-wide key is a setting: the note surface itself needs nothing configured.
public sealed record QuickNoteSettings
{
    // Off by default, like every other desktop-wide key: it is taken from every other application on the machine.
    public bool GlobalHotkeyEnabled { get; init; }

    // Avalonia `Key` enum name. F7 sits below the screenshot's F8 and beside nothing the desktops we run on claim.
    public string HotkeyKeyName { get; init; } = "F7";
}
