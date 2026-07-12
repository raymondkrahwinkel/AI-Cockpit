namespace Cockpit.Infrastructure.Configuration;

/// <summary>
/// The modifier values the old <c>sessionSwitching</c> section could hold, kept only so an existing
/// <c>cockpit.json</c> still parses and can be carried over to the session-switch shortcuts.
/// </summary>
internal enum LegacySessionSwitchModifier
{
    Ctrl,
    CtrlAlt,
    Alt,
}
