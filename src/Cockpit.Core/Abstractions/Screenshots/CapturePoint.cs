namespace Cockpit.Core.Abstractions.Screenshots;

// A whole-number position, in one of the two coordinate spaces a capture lives in: the desktop the operator
// points at, or the pixels of the image that came back. Which one a value is in is the property or parameter's
// to say — the two are the same shape and are not interchangeable, which is the point of
// `CapturedDisplay.ToImagePixel` existing at all.
// Deliberately not Avalonia's `PixelPoint`: this lives in Core, which no UI framework reaches into, and the
// capture implementations behind it are D-Bus, a Win32 blit and a helper process rather than anything drawn.
public readonly record struct CapturePoint(int X, int Y);
