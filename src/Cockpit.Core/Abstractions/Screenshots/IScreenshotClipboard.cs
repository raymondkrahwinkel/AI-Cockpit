namespace Cockpit.Core.Abstractions.Screenshots;

/// <summary>
/// The system clipboard, as the screenshot flow needs it — the road an image takes into a terminal session
/// (AC-226). A pty carries bytes, but the TUI reads the system clipboard itself when it sees a paste, so getting
/// the image there is the half a program cannot do without touching the clipboard, and performing the paste is
/// the other half.
/// </summary>
/// <remarks>
/// Write-only since AC-341. It could once read as well, because the Windows capture came off the clipboard:
/// <c>ms-screenclip:</c> opened the native Snip overlay, put the result there and told the app that asked for it
/// nothing at all. AC-327 replaced that with a capture of our own, and the read had no caller left that could
/// fire.
/// <para>
/// The clipboard belongs to the windowing layer, so the implementation lives with the views (Avalonia's
/// <c>IClipboard</c> hangs off a <c>TopLevel</c>) while the session that needs it lives here. This interface is
/// the seam between them.
/// </para>
/// </remarks>
public interface IScreenshotClipboard
{
    /// <summary>
    /// Puts an image on the clipboard, replacing whatever was there, and leaves it there in a form another
    /// program can actually read. Returns whether it landed — false when the clipboard could not be written,
    /// which is a thing the caller has to tell the operator rather than assume.
    /// </summary>
    /// <remarks>
    /// It really does replace what the operator had copied. That is inherent to the route, not an oversight: the
    /// clipboard is the only channel the TUI reads an image from, and there is no way to hand it one privately.
    /// </remarks>
    Task<bool> TrySetImageAsync(byte[] png, CancellationToken cancellationToken = default);
}
