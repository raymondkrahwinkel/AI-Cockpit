namespace Cockpit.Core.Abstractions.Screenshots;

/// <summary>
/// Reads an image off the system clipboard (AC-220). Exists because the Windows capture route has no other
/// way home: <c>ms-screenclip:</c> opens the native Snip overlay and puts the result on the clipboard, and
/// tells the app that asked for it nothing at all — not when it finished, not whether it was cancelled.
/// </summary>
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
}
