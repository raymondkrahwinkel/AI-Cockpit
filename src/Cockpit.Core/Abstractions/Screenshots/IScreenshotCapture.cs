namespace Cockpit.Core.Abstractions.Screenshots;

/// <summary>
/// Reads the screen — every display, no UI of its own — and hands back the pixels with the layout they came off
/// (AC-333), one implementation per OS. The desktop's own picker used to do this (a bare PNG), until AC-220's live
/// test showed a portal hands back whatever backend UI the machine runs (a KDE dialog, not a crosshair); the
/// selection is the cockpit's own from here on (AC-329), distinct from <c>Screenshotter</c> (the cockpit's own
/// Avalonia tree). Never wait on this synchronously from the UI thread: interim implementations hand control to the desktop's picker, and on Windows that picker needs the thread a sync wait would hold.
/// </summary>
public interface IScreenshotCapture
{
    /// <summary>
    /// Whether this platform can capture at all. Reported rather than discovered by trying, because a capture
    /// that yields nothing is otherwise indistinguishable from an operator who changed their mind — and a button
    /// that cannot say which is a dead control.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    /// Completes once <see cref="IsSupported"/> has settled — immediately on a platform that knows the answer
    /// outright, and after a round trip to the desktop on Linux, where only the session bus can say whether a
    /// screenshot portal is served (AC-326). A caller reading <see cref="IsSupported"/> before this completes gets
    /// "no" from a machine that may well capture, so the cockpit wires its button both from whatever is known now, and again from here — never once at startup only.
    /// </summary>
    Task SupportSettled { get; }

    /// <summary>
    /// Reads every display and returns the composed image with the layout behind it, or <see langword="null"/>
    /// when there was nothing to capture. A capture that genuinely breaks — portal refuses, helper process won't
    /// start, no implementation at all — throws, so the caller can say which; null is for a read that simply
    /// produced no image, which the interim picker-backed implementations still use for a cancelled selection until AC-326, AC-327 and AC-328 take the picker out of the path.
    /// </summary>
    Task<ScreenCapture?> CaptureAsync(CancellationToken cancellationToken = default);
}
