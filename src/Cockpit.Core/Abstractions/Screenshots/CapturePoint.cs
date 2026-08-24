namespace Cockpit.Core.Abstractions.Screenshots;

// AC-1013: A whole-number position in one of the capture's two coordinate spaces (desktop or image pixels) —
// not interchangeable, hence `CapturedDisplay.ToImagePixel`. Deliberately not Avalonia's `PixelPoint`: this
// lives in Core, which no UI framework reaches into, and the captures behind it (D-Bus, Win32 blit, helper) aren't drawn.
public readonly record struct CapturePoint(int X, int Y);
