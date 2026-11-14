using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Cockpit.Plugin.Browser.Ipc;

namespace Cockpit.Plugin.Browser.UI.Engine;

internal sealed partial class HostBrowserView
{
    private WriteableBitmap? _popupBitmap;
    private Rect _popupRect;
    private long _popupRevision;

    private void _ClearPopup()
    {
        _popupBitmap?.Dispose();
        _popupBitmap = null;
        InvalidateVisual();
    }

    private void _PresentPopup(BrowserTabPopup popup)
    {
        if (popup.Revision <= _popupRevision || popup.SizeRevision < _size.Revision || _faulted || _closing)
        {
            return;
        }
        if (popup.SizeRevision != _size.Revision || popup.Scale != _size.Scale || popup.Revision < 1)
        {
            throw new InvalidDataException("Browser popup correlation failed.");
        }
        _popupRevision = popup.Revision;
        if (popup.Visible == false || popup.Buffer is null)
        {
            _ClearPopup();
            return;
        }
        var descriptor = popup.Buffer;
        if (descriptor.Generation != HostGeneration || descriptor.TabId != TabId || popup.Width <= 0 || popup.Height <= 0 ||
            descriptor.Width != (int)Math.Ceiling(popup.Width * popup.Scale) || descriptor.Height != (int)Math.Ceiling(popup.Height * popup.Scale))
        {
            throw new InvalidDataException("Invalid browser popup buffer.");
        }
        using var buffer = _host.Connection.OpenFrameBuffer(_host.BufferDirectory, descriptor, TabId);
        _ClearPopup();
        _popupBitmap = new WriteableBitmap(new PixelSize(descriptor.Width, descriptor.Height), new Vector(96 * popup.Scale, 96 * popup.Scale),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var target = _popupBitmap.Lock())
        {
            buffer.CopyRectangle(target.Address, target.RowBytes, 0, 0, descriptor.Width, descriptor.Height, write: false);
        }
        _popupRect = new Rect(popup.X, popup.Y, popup.Width, popup.Height);
        InvalidateVisual();
    }
}
