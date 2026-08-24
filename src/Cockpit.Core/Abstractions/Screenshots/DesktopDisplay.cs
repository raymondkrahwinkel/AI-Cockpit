namespace Cockpit.Core.Abstractions.Screenshots;

// AC-326: display layout as the desktop reports it, before pixels exist — the half of `CapturedDisplay`
// knowable pre-capture. Needed because the Linux portal (unlike Windows/macOS) hands back one image with no
// layout info, so layout must be reconciled with the image separately.
public sealed record DesktopDisplay
{
    // Where this display sits on the virtual desktop, in that desktop's own coordinates — the same space as `CapturedDisplay.DesktopBounds`.
    public required CaptureRect Bounds { get; init; }

    // What the desktop reports as this display's scale factor — 1.0, 1.5, 2.0.
    public required double Scale { get; init; }
}
