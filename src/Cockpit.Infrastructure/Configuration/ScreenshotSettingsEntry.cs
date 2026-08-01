using Cockpit.Core.Abstractions.Screenshots;
using Cockpit.Core.Screenshots;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>On-disk shape of <see cref="ScreenshotSettings"/> in the <c>screenshots</c> section of <c>cockpit.json</c>.</summary>
internal sealed class ScreenshotSettingsEntry
{
    public bool GlobalHotkeyEnabled { get; set; }

    public string HotkeyKeyName { get; set; } = "F8";

    /// <summary>The last region as four numbers, so the file stays readable and a hand-edited one cannot land a half-built rectangle in memory.</summary>
    public int[]? LastRegion { get; set; }

    public bool PreviewEnabled { get; set; }

    public static ScreenshotSettingsEntry FromDomain(ScreenshotSettings settings) => new()
    {
        GlobalHotkeyEnabled = settings.GlobalHotkeyEnabled,
        HotkeyKeyName = settings.HotkeyKeyName,
        LastRegion = settings.LastRegion is { } region ? [region.X, region.Y, region.Width, region.Height] : null,
        PreviewEnabled = settings.PreviewEnabled,
    };

    public ScreenshotSettings ToDomain() => new()
    {
        GlobalHotkeyEnabled = GlobalHotkeyEnabled,
        // An empty key in the file would arm nothing and report nothing, which reads as a broken hotkey rather
        // than an unset one. Fall back to the default the fresh install would have had.
        HotkeyKeyName = string.IsNullOrWhiteSpace(HotkeyKeyName) ? new ScreenshotSettings().HotkeyKeyName : HotkeyKeyName,
        // Anything other than four numbers is not a rectangle. A hand-edited file that got it wrong loses the
        // convenience of a remembered region, which is the harmless half of the choice.
        LastRegion = LastRegion is [var x, var y, var width, var height] ? new CaptureRect(x, y, width, height) : null,
        PreviewEnabled = PreviewEnabled,
    };
}
