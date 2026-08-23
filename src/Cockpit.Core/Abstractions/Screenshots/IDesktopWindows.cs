namespace Cockpit.Core.Abstractions.Screenshots;

/// <summary>
/// The windows on the desktop, front to back (AC-330) — what the selection surface needs to highlight and crop the one
/// under the pointer. Cannot be built everywhere: Windows/macOS/X11 publish stacking order; Wayland doesn't, and this
/// app runs there as an XWayland client seeing only XWayland windows. Hence <see cref="IsSupported"/>: present, or absent and saying so.
/// </summary>
public interface IDesktopWindows
{
    /// <summary>
    /// Whether this desktop will say where its windows are. False is an answer, and the surface shows it as one.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    /// The windows the operator can see, front-most first, in the same coordinates
    /// <see cref="CapturedDisplay.DesktopBounds"/> uses. Empty where <see cref="IsSupported"/> is false.
    /// </summary>
    IReadOnlyList<DesktopWindow> Enumerate();
}
