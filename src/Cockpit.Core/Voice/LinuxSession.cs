namespace Cockpit.Core.Voice;

/// <summary>
/// Pure kernel for the one question the global-hotkey registration turns on (#34), deliberately free of any
/// environment access so it is unit-testable: the caller reads the two variables, this decides what they mean.
/// </summary>
/// <remarks>
/// It exists because the answer decides whether a Linux desktop gets a working hotkey at all — under Wayland
/// nothing may install a keyboard hook, so the XDG GlobalShortcuts portal is the only route; under X11 the hook
/// Windows uses works, and most X11 desktops have no GlobalShortcuts implementation to fall back on. Read from the
/// environment, that answer is untestable on any machine that is not the session in question: a CI runner sets
/// neither variable, so it can only ever exercise the X11 arm, and the Wayland arm was carried by nothing but the
/// reasoning in a comment.
/// </remarks>
public static class LinuxSession
{
    /// <summary>
    /// Whether this Linux session is Wayland.
    /// </summary>
    /// <param name="xdgSessionType">
    /// <c>XDG_SESSION_TYPE</c> — what the session's own login sets, and what every desktop reports itself by.
    /// </param>
    /// <param name="waylandDisplay">
    /// <c>WAYLAND_DISPLAY</c> — the socket actually being talked to. It covers a session that never set the first,
    /// which is why it is a fallback and not a second opinion: a compositor is running either way.
    /// </param>
    /// <returns>False when neither says Wayland — X11, the older and plainer case, where a keyboard hook works.</returns>
    public static bool IsWayland(string? xdgSessionType, string? waylandDisplay) =>
        string.Equals(xdgSessionType, "wayland", StringComparison.OrdinalIgnoreCase)
        || !string.IsNullOrEmpty(waylandDisplay);
}
