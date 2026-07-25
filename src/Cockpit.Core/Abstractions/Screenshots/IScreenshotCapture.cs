namespace Cockpit.Core.Abstractions.Screenshots;

/// <summary>
/// Takes a screenshot the way the desktop's own screenshot tool does — drag a region, pick a window, or
/// take the whole screen — and hands back the PNG bytes (AC-220). One implementation per OS, selected in
/// <c>Cockpit.Infrastructure.DependencyInjection</c> the same way <c>IGlobalHotkeyService</c> is: the XDG
/// <c>Screenshot</c> portal on Linux, <c>screencapture -i</c> on macOS, the Snip overlay on Windows.
/// </summary>
/// <remarks>
/// This is the first capture of anything <em>outside</em> the cockpit. <c>Screenshotter</c> renders the
/// cockpit's own Avalonia tree to a PNG, which is a different thing entirely and stays where it is.
/// <para>
/// Threading: the capture hands control to the OS picker, so it takes exactly as long as the operator does —
/// seconds, not milliseconds. It must not be waited on synchronously from the UI thread, which on Windows is
/// the thread the picker overlay itself needs.
/// </para>
/// </remarks>
public interface IScreenshotCapture
{
    /// <summary>
    /// Whether this platform can capture at all. Reported rather than discovered by trying, because a capture
    /// that yields nothing is otherwise indistinguishable from an operator who pressed Escape — and a button
    /// that cannot say which is a dead control.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    /// Runs the OS's interactive picker and returns the captured image as PNG bytes, or <see langword="null"/>
    /// when nothing was captured — the operator cancelled, or this platform has no capture at all.
    /// </summary>
    /// <remarks>
    /// A cancel is an answer, not a failure, so it returns null rather than throwing: pressing Escape on the
    /// picker is the ordinary way to change your mind. A capture that genuinely breaks (the portal refuses, the
    /// helper process cannot be started) does throw, so the caller can say so instead of showing the same
    /// silence a cancel produces.
    /// </remarks>
    Task<byte[]?> CaptureInteractiveAsync(CancellationToken cancellationToken = default);
}
