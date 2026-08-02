namespace Cockpit.Infrastructure.Configuration;

// Legacy on-disk shape of the `sessionSwitching` section. The session switch used to be a setting of its
// own — a master on/off plus a modifier, arrowed by a hard-coded key handler — before it became two ordinary
// shortcuts (Options → Shortcuts). Kept read-only so `ShortcutSettingsStore` can translate a config
// written by an older build into gestures rather than silently resetting the operator's choice to the default;
// nothing writes this section any more.
internal sealed class SessionSwitchSettingsEntry
{
    public bool IsEnabled { get; set; } = true;

    public LegacySessionSwitchModifier Modifier { get; set; } = LegacySessionSwitchModifier.Ctrl;
}
