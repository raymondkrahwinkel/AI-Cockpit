using Cockpit.Core.QuickNotes;

namespace Cockpit.Infrastructure.Configuration;

// On-disk shape of `QuickNoteSettings` in the `quickNotes` section of `cockpit.json`.
internal sealed class QuickNoteSettingsEntry
{
    public bool GlobalHotkeyEnabled { get; set; }

    public string HotkeyKeyName { get; set; } = "F7";

    public static QuickNoteSettingsEntry FromDomain(QuickNoteSettings settings) => new()
    {
        GlobalHotkeyEnabled = settings.GlobalHotkeyEnabled,
        HotkeyKeyName = settings.HotkeyKeyName,
    };

    // An empty key in the file would arm nothing and report nothing; fall back to the fresh-install default.
    public QuickNoteSettings ToDomain() => new()
    {
        GlobalHotkeyEnabled = GlobalHotkeyEnabled,
        HotkeyKeyName = string.IsNullOrWhiteSpace(HotkeyKeyName) ? new QuickNoteSettings().HotkeyKeyName : HotkeyKeyName,
    };
}
