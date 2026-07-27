namespace Cockpit.Core.Abstractions.Screenshots;

/// <summary>
/// Reads the screen — every display, no UI of its own — and hands back the pixels together with the layout they
/// came off (AC-333). One implementation per OS, selected in <c>Cockpit.Infrastructure.DependencyInjection</c>
/// the same way <c>IGlobalHotkeyService</c> is.
/// </summary>
/// <remarks>
/// The desktop's own picker used to be what this ran, which is why it once returned a bare PNG: someone else had
/// already chosen the region. AC-220's live test killed that — a portal hands back the UI of whatever backend the
/// machine happens to run, so the operator got a KDE dialog with three clicks in it rather than a crosshair. The
/// selection is the cockpit's own from here on (AC-329), and it needs pixels and a layout to work from, which is
/// what this returns.
/// <para>
/// This is the first capture of anything <em>outside</em> the cockpit. <c>Screenshotter</c> renders the cockpit's
/// own Avalonia tree to a PNG, which is a different thing entirely and stays where it is.
/// </para>
/// <para>
/// Threading: never wait on this synchronously from the UI thread. Reading every display is quick, but the interim
/// implementations still hand control to the desktop's picker and so take exactly as long as the operator does —
/// and on Windows that picker needs the very thread a synchronous wait would be holding.
/// </para>
/// </remarks>
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
    /// outright, and after a round trip to the desktop on Linux, where nothing but the session bus can say
    /// whether a screenshot portal is being served (AC-326).
    /// </summary>
    /// <remarks>
    /// A caller that reads <see cref="IsSupported"/> before this completes gets "no" from a machine that may
    /// well be able to capture, and the cockpit wires the composer's button in the same statement that builds
    /// this — so a button disabled once at startup would stay disabled for the rest of the run. Wire from
    /// whatever is known now, and again from here.
    /// </remarks>
    Task SupportSettled { get; }

    /// <summary>
    /// Reads every display and returns the composed image with the layout behind it, or <see langword="null"/>
    /// when there was nothing to capture.
    /// </summary>
    /// <remarks>
    /// A capture that genuinely breaks — the portal refuses, a helper process cannot be started, the platform has
    /// no implementation at all — throws, so the caller can say which. The null is for the case where the read
    /// simply produced no image, which the interim picker-backed implementations still use for a cancelled
    /// selection until AC-326, AC-327 and AC-328 take the picker out of the path.
    /// </remarks>
    Task<ScreenCapture?> CaptureAsync(CancellationToken cancellationToken = default);
}
