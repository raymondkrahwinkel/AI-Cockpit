namespace Cockpit.Infrastructure.Configuration;

// Legacy on-disk shape of `sessionSwitching`, from before it became two ordinary shortcuts. Kept
// read-only so `ShortcutSettingsStore` can translate an older build's config instead of resetting it.
internal sealed class SessionSwitchSettingsEntry
{
    public bool IsEnabled { get; set; } = true;

    public LegacySessionSwitchModifier Modifier { get; set; } = LegacySessionSwitchModifier.Ctrl;
}
