using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.App.Services;

/// <summary>
/// Writes a capture to the system clipboard through Avalonia, which is how it reaches a terminal session
/// (AC-226). Lives here rather than in Infrastructure because Avalonia's clipboard hangs off a window, and the
/// only window is this app's.
/// </summary>
/// <remarks>
/// Every write is marshalled to the UI thread: the capture finishes on a background task, and on Windows the
/// clipboard is owned by the thread that pumps messages.
/// </remarks>
internal sealed class AvaloniaScreenshotClipboard : IScreenshotClipboard, ISingletonService
{
    public Task<bool> TrySetImageAsync(byte[] png, CancellationToken cancellationToken = default) =>
        Dispatcher.UIThread.InvokeAsync(() => _Clipboard() is { } clipboard
            ? WriteAsync(clipboard, png)
            : Task.FromResult(false)).WaitAsync(cancellationToken);

    /// <summary>
    /// Puts the image on <paramref name="clipboard"/> so that another program can read it back. Internal for
    /// the test that holds the flush in place; production goes through <see cref="TrySetImageAsync"/>.
    /// </summary>
    internal static async Task<bool> WriteAsync(IClipboard clipboard, byte[] png)
    {
        try
        {
            using var stream = new MemoryStream(png);
            using var bitmap = new Bitmap(stream);
            await clipboard.SetBitmapAsync(bitmap);

            // The set on its own only promises the image: Avalonia's Win32 clipboard hands the OS a data object
            // that renders on demand, and until it is flushed there is nothing for anyone to redeem — not the
            // terminal, not a manual CTRL+V, not Paint. That is what AC-341 was, and it is also what made a
            // capture appear to *destroy* the clipboard (measured 2026-07-25): the promise replaced whatever the
            // operator had copied and then answered no one. Flushing while the bitmap is still alive is the whole
            // point — it is what forces the render — so this cannot move out of the using above.
            await clipboard.FlushAsync();
            return true;
        }
        catch (Exception)
        {
            // Another application can hold the clipboard locked, and a capture that will not decode is not an
            // image to hand on. Either way it did not land, which is what the caller asked — and what it will
            // tell the operator, rather than sending a paste key for an image that is not there.
            return false;
        }
    }

    private static IClipboard? _Clipboard() =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window }
            ? window.Clipboard
            : null;
}
