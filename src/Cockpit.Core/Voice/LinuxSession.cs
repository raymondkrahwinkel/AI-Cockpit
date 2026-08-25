namespace Cockpit.Core.Voice;

// AC-1013: Pure kernel for the global-hotkey registration's Wayland-vs-X11 question (#34), free of environment access so it's unit-testable — under Wayland only the XDG GlobalShortcuts portal works, under X11 the Windows-style hook works; reading env vars directly would make the Wayland arm untestable on CI (which never sets either variable).
public static class LinuxSession
{
    // Whether this Linux session is Wayland. `xdgSessionType` (XDG_SESSION_TYPE) is the session's own report;
    // `waylandDisplay` (WAYLAND_DISPLAY) is a fallback covering a session that never set the first. False
    // (X11) when neither says Wayland.
    public static bool IsWayland(string? xdgSessionType, string? waylandDisplay) =>
        string.Equals(xdgSessionType, "wayland", StringComparison.OrdinalIgnoreCase)
        || !string.IsNullOrEmpty(waylandDisplay);
}
