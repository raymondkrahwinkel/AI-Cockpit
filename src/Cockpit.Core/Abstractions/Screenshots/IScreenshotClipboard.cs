namespace Cockpit.Core.Abstractions.Screenshots;

/// <summary>
/// The system clipboard, as the screenshot flow needs it (AC-220) — read for the Windows capture route, write
/// for the terminal one.
/// </summary>
/// <remarks>
/// Reading exists because <c>ms-screenclip:</c> has no other way home: it opens the native Snip overlay, puts
/// the result on the clipboard, and tells the app that asked for it nothing at all — not when it finished, not
/// whether it was cancelled.
/// <para>
/// Writing exists because that is how an image reaches a terminal session (AC-226). A pty carries bytes, but the
/// claude TUI reads the system clipboard itself when it sees a paste key — so putting the image there and
/// sending the key is precisely what an operator does by hand, and the only part of it a program cannot do
/// without touching the clipboard.
/// </para>
/// </remarks>
/// <remarks>
/// The clipboard belongs to the windowing layer, so the implementation lives with the views (Avalonia's
/// <c>IClipboard</c> hangs off a <c>TopLevel</c>) while the capture that needs it lives in Infrastructure.
/// This interface is the seam between them, and what lets the Windows capture be tested without a desktop.
/// </remarks>
public interface IScreenshotClipboard
{
    /// <summary>
    /// The image currently on the clipboard as PNG bytes, or <see langword="null"/> when there is none — or the
    /// clipboard could not be read, since another application can hold it locked. A clipboard that will not
    /// answer is the same as one holding no image for every caller here, so that is not an error either.
    /// Cancellation is the one thing it does raise, as any awaitable does.
    /// </summary>
    Task<byte[]?> TryReadImageAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts an image on the clipboard, replacing whatever was there. Returns whether it landed — false when the
    /// clipboard could not be written, which is a thing the caller has to tell the operator rather than assume.
    /// </summary>
    /// <remarks>
    /// It really does replace what the operator had copied. That is inherent to the route, not an oversight: the
    /// clipboard is the only channel the TUI reads an image from, and there is no way to hand it one privately.
    /// </remarks>
    Task<bool> TrySetImageAsync(byte[] png, CancellationToken cancellationToken = default);
}
