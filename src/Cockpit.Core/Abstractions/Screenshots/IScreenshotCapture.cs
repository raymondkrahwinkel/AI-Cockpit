namespace Cockpit.Core.Abstractions.Screenshots;

/// <summary>
/// Reads the screen — every display, no UI of its own — and hands back the pixels with their layout (AC-333), one
/// implementation per OS. The desktop's own picker did this until AC-220 showed a portal hands back whatever backend
/// UI the machine runs; selection is the cockpit's own since (AC-329). Never wait on this synchronously — Windows' picker needs the UI thread.
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
    /// Completes once <see cref="IsSupported"/> has settled — immediately where the answer is known outright, and after
    /// a round trip to the desktop on Linux, where only the session bus can say (AC-326). Reading <see cref="IsSupported"/>
    /// before this completes can read "no" for a machine that may well capture, so the button wires from both.
    /// </summary>
    Task SupportSettled { get; }

    /// <summary>
    /// Reads every display and returns the composed image with its layout, or <see langword="null"/> when there was
    /// nothing to capture. A capture that genuinely breaks throws, so the caller can say which; null is for a plain no-image
    /// read, which interim picker-backed implementations use for a cancelled selection until AC-326/327/328 land.
    /// </summary>
    Task<ScreenCapture?> CaptureAsync(CancellationToken cancellationToken = default);
}
