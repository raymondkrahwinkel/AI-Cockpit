using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.Input;

namespace Cockpit.App.ViewModels;

// A pending image attached to the next user message, shown as a removable thumbnail chip above
// the input. Holds the PNG bytes for the wire plus a decoded `Thumbnail` for preview.
public partial class ImageAttachmentViewModel : ViewModelBase, IDisposable
{
    private readonly Action<ImageAttachmentViewModel> _onRemove;
    private bool _disposed;

    // The pasted image as PNG bytes — sent to the session as a base64 image block.
    public byte[] PngBytes { get; }

    public string MediaType => "image/png";

    // Decoded preview bitmap for the chip; the same PNG bytes, decoded once for display.
    public Bitmap Thumbnail { get; }

    // Test seam: true once Dispose has freed the thumbnail. Guards the leak-fix regression test without
    // reaching into a disposed Avalonia bitmap's internals.
    internal bool IsDisposed => _disposed;

    public ImageAttachmentViewModel(byte[] pngBytes, Action<ImageAttachmentViewModel> onRemove)
    {
        PngBytes = pngBytes;
        _onRemove = onRemove;
        using var stream = new MemoryStream(pngBytes);
        Thumbnail = new Bitmap(stream);
    }

    [RelayCommand]
    private void Remove() => _onRemove(this);

    // Frees the decoded preview bitmap deterministically. Call ONLY when the chip is gone (removed
    // from PendingAttachments or the list cleared on send) — the bitmap is bound to a visible Image
    // control, so disposing while the chip still shows blanks the thumbnail. Idempotent.
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Thumbnail.Dispose();
        GC.SuppressFinalize(this);
    }
}
