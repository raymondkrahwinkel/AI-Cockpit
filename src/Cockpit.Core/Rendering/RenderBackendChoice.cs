namespace Cockpit.Core.Rendering;

/// <summary>
/// The render backend the operator picks for the app to draw with (AC-67). Only meaningful on macOS, where it
/// maps to Avalonia's native rendering mode; on Windows/Linux it is inert. <see cref="Auto"/> leaves Avalonia's
/// own <c>UsePlatformDetect()</c> selection alone (Metal on macOS), and is the default.
/// </summary>
public enum RenderBackendChoice
{
    Auto,
    Metal,
    OpenGl,
    Software,
}
