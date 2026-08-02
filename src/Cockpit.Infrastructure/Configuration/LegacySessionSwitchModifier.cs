namespace Cockpit.Infrastructure.Configuration;

// The modifier values the old `sessionSwitching` section could hold, kept only so an existing
// `cockpit.json` still parses and can be carried over to the session-switch shortcuts.
internal enum LegacySessionSwitchModifier
{
    Ctrl,
    CtrlAlt,
    Alt,
}
