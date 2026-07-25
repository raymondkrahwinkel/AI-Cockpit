using Cockpit.Core.Screenshots;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>On-disk shape of <see cref="ScreenshotSettings"/> in the <c>screenshots</c> section of <c>cockpit.json</c>.</summary>
internal sealed class ScreenshotSettingsEntry
{
    public bool GlobalHotkeyEnabled { get; set; }

    public string HotkeyKeyName { get; set; } = "F8";

    public static ScreenshotSettingsEntry FromDomain(ScreenshotSettings settings) => new()
    {
        GlobalHotkeyEnabled = settings.GlobalHotkeyEnabled,
        HotkeyKeyName = settings.HotkeyKeyName,
    };

    public ScreenshotSettings ToDomain() => new()
    {
        GlobalHotkeyEnabled = GlobalHotkeyEnabled,
        // An empty key in the file would arm nothing and report nothing, which reads as a broken hotkey rather
        // than an unset one. Fall back to the default the fresh install would have had.
        HotkeyKeyName = string.IsNullOrWhiteSpace(HotkeyKeyName) ? new ScreenshotSettings().HotkeyKeyName : HotkeyKeyName,
    };
}
