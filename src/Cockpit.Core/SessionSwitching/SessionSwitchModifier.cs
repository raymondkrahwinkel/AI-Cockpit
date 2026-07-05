namespace Cockpit.Core.SessionSwitching;

/// <summary>
/// The modifier key combination that, together with an arrow key, switches the active session.
/// Kept modifier-only (not the full arrow gesture) because the arrow direction is fixed
/// (Left/Up = previous, Right/Down = next); the user only picks which modifier arms it.
/// </summary>
public enum SessionSwitchModifier
{
    /// <summary>Ctrl + arrow (the default).</summary>
    Ctrl,

    /// <summary>Ctrl + Alt + arrow — for users who keep plain Ctrl+arrow free for word navigation everywhere.</summary>
    CtrlAlt,

    /// <summary>Alt + arrow.</summary>
    Alt,
}
