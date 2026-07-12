namespace Cockpit.Infrastructure.Configuration;

/// <summary>
/// Legacy on-disk shape of the <c>sessionSwitching</c> section. The session switch used to be a setting of its
/// own — a master on/off plus a modifier, arrowed by a hard-coded key handler — before it became two ordinary
/// shortcuts (Options → Shortcuts). Kept read-only so <c>ShortcutSettingsStore</c> can translate a config
/// written by an older build into gestures rather than silently resetting the operator's choice to the default;
/// nothing writes this section any more.
/// </summary>
internal sealed class SessionSwitchSettingsEntry
{
    public bool IsEnabled { get; set; } = true;

    public LegacySessionSwitchModifier Modifier { get; set; } = LegacySessionSwitchModifier.Ctrl;
}
