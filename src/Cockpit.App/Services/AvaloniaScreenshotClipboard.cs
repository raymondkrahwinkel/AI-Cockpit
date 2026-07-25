using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.App.Services;

/// <summary>
/// Reads the system clipboard through Avalonia for the Windows screenshot capture (AC-220), which has no other
/// way of learning that a snip happened. Lives here rather than in Infrastructure because Avalonia's clipboard
/// hangs off a window, and the only window is this app's.
/// </summary>
/// <remarks>
/// Every read is marshalled to the UI thread: the capture polls from a background task, and on Windows the
/// clipboard is owned by the thread that pumps messages. The same encode the CTRL+V paste path uses
/// (<c>Bitmap.Save</c> to a memory stream) produces the PNG bytes, so a snipped image and a pasted one arrive
/// at the session in exactly the same shape.
/// </remarks>
internal sealed class AvaloniaScreenshotClipboard : IScreenshotClipboard, ISingletonService
{
    public Task<byte[]?> TryReadImageAsync(CancellationToken cancellationToken = default) =>
        Dispatcher.UIThread.InvokeAsync(_ReadAsync).WaitAsync(cancellationToken);

    private static async Task<byte[]?> _ReadAsync()
    {
        if (_Clipboard() is not { } clipboard)
        {
            return null;
        }

        try
        {
            using var bitmap = await clipboard.TryGetBitmapAsync();
            if (bitmap is null)
            {
                return null;
            }

            using var stream = new MemoryStream();
            bitmap.Save(stream);
            return stream.ToArray();
        }
        catch (Exception)
        {
            // Another application can hold the clipboard locked, and a format it advertises can fail to decode.
            // Either way there is no image to be had right now, which is what the caller is asking; it polls, so
            // a momentary lock resolves itself on the next pass.
            return null;
        }
    }

    private static IClipboard? _Clipboard() =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window }
            ? window.Clipboard
            : null;
}
