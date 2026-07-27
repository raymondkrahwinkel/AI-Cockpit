namespace Cockpit.Core.Abstractions.Screenshots;

/// <summary>
/// A display as the desktop itself reports it (AC-326): where it sits and what it is scaled by, with nothing yet
/// said about pixels. The half of <see cref="CapturedDisplay"/> that is knowable before a capture exists.
/// </summary>
/// <remarks>
/// A capture that composes its own image — Windows' virtual-screen blit, macOS' per-display files — knows where
/// every display's pixels landed because it put them there. The Linux portal does not: it hands back one image
/// and says nothing about what went into it, so the layout has to come from the desktop separately and be
/// reconciled with the image afterwards. This is what comes back from that ask.
/// </remarks>
public sealed record DesktopDisplay
{
    /// <summary>Where this display sits on the virtual desktop, in that desktop's own coordinates — the same space as <see cref="CapturedDisplay.DesktopBounds"/>.</summary>
    public required CaptureRect Bounds { get; init; }

    /// <summary>What the desktop reports as this display's scale factor — 1.0, 1.5, 2.0.</summary>
    public required double Scale { get; init; }
}
