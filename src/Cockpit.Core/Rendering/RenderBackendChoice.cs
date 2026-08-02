namespace Cockpit.Core.Rendering;

// The render backend the operator picks for the app to draw with (AC-67). Only meaningful on macOS, where it
// maps to Avalonia's native rendering mode; on Windows/Linux it is inert. `Auto` leaves Avalonia's
// own `UsePlatformDetect()` selection alone (Metal on macOS), and is the default.
public enum RenderBackendChoice
{
    Auto,
    Metal,
    OpenGl,
    Software,
}
