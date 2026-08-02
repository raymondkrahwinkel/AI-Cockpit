using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.Input;

namespace Cockpit.App.ViewModels;

// A pending image attached to the next user message, shown as a removable thumbnail chip above
// the input. Holds the PNG bytes for the wire plus a decoded `Thumbnail` for preview.
public partial class ImageAttachmentViewModel : ViewModelBase
{
    private readonly Action<ImageAttachmentViewModel> _onRemove;

    // The pasted image as PNG bytes — sent to the session as a base64 image block.
    public byte[] PngBytes { get; }

    public string MediaType => "image/png";

    // Decoded preview bitmap for the chip; the same PNG bytes, decoded once for display.
    public Bitmap Thumbnail { get; }

    public ImageAttachmentViewModel(byte[] pngBytes, Action<ImageAttachmentViewModel> onRemove)
    {
        PngBytes = pngBytes;
        _onRemove = onRemove;
        using var stream = new MemoryStream(pngBytes);
        Thumbnail = new Bitmap(stream);
    }

    [RelayCommand]
    private void Remove() => _onRemove(this);
}
