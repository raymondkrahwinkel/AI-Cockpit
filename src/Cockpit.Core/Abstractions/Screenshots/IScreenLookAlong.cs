namespace Cockpit.Core.Abstractions.Screenshots;

// The look-along the operator started with a click: which display, until when, and when the assistant last looked.
public sealed record ScreenLookSession(CaptureRect Display, DateTimeOffset Until, DateTimeOffset? LastLook);

// AC-2209: what the desktop sees of the assistant looking at one whole display, so the frontend draws the strip's
// border, the frame and the tab without reaching into Infrastructure.
public interface IScreenLookAlong
{
    // Raised when a session starts, ends or takes a look.
    event EventHandler? Changed;

    // Set by the desktop where the OS cannot leave its signals out of a capture: hides them, returns what shows them again.
    Func<CancellationToken, Task<Action>>? HideSignals { get; set; }

    // The running session, or null once it was stopped or its timer ran out.
    ScreenLookSession? Current { get; }

    void Stop();
}
