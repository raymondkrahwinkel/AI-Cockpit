namespace Cockpit.Core.Abstractions.Screenshots;

/// <summary>
/// The windows on the desktop, front to back (AC-330) — what the selection surface needs to highlight the one
/// under the pointer and crop it out of the capture it already has. The one mode that cannot be built the same
/// way everywhere: Windows, macOS and a real X11 session all publish window geometry and stacking order; Wayland
/// deliberately does not, and this app runs there as an XWayland client seeing only other XWayland windows — a
/// picker built on that would offer a fraction of the operator's windows, worse than none. Hence <see cref="IsSupported"/>: present and working, or absent and saying so.
/// </summary>
public interface IDesktopWindows
{
    /// <summary>Whether this desktop will say where its windows are. False is an answer, and the surface shows it as one.</summary>
    bool IsSupported { get; }

    /// <summary>
    /// The windows the operator can see, front-most first, in the same coordinates
    /// <see cref="CapturedDisplay.DesktopBounds"/> uses. Empty where <see cref="IsSupported"/> is false.
    /// </summary>
    IReadOnlyList<DesktopWindow> Enumerate();
}
