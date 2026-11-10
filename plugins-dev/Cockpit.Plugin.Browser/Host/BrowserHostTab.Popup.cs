using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using Cockpit.Plugin.Browser.Ipc;
using Xilium.CefGlue;

namespace Cockpit.Plugin.Browser.Host;

internal sealed partial class BrowserHostTab
{
    private readonly Channel<PopupPaint> _popups = Channel.CreateBounded<PopupPaint>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Task _popupSender;
    private readonly Channel<BrowserTabNewTab> _newTabs = Channel.CreateBounded<BrowserTabNewTab>(64);
    private readonly Task _newTabSender;
    private bool _popupVisible;
    private CefRectangle _popupRect;
    private long _popupRevision;

    private void _DismissPopup(CefBrowserHost host)
    {
        if (_popupVisible)
        {
            host.SendKeyEvent(new CefKeyEvent { EventType = CefKeyEventType.RawKeyDown, WindowsKeyCode = 27 });
            host.SendKeyEvent(new CefKeyEvent { EventType = CefKeyEventType.KeyUp, WindowsKeyCode = 27 });
        }
        _PopupShow(false);
    }

    private void _NewTab(string address, bool userGesture)
    {
        lock (_control)
        {
            // A shared tab's grant and origin boundary must never escape into a fresh, uncontrolled tab.
            if (_closing || _controlOrigin is not null || userGesture == false || Uri.TryCreate(address, UriKind.Absolute, out var uri) == false ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps && address != "about:blank"))
            {
                return;
            }
            if (_newTabs.Writer.TryWrite(new BrowserTabNewTab(address, _controlRevision)) == false)
            {
                _connection.Close();
            }
        }
    }

    private async Task _SendNewTabsAsync()
    {
        try
        {
            await foreach (var request in _newTabs.Reader.ReadAllAsync())
            {
                if (_closing == false && _controlOrigin is null && request.ControlRevision == Interlocked.Read(ref _controlRevision))
                {
                    await _CallAsync("tab.newTab", JsonSerializer.SerializeToElement(request));
                }
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            _connection.Close();
        }
    }

    private void _PopupShow(bool visible)
    {
        _popupVisible = visible && _closing == false;
        _PopupPublish();
    }

    private void _PopupSize(CefRectangle rect)
    {
        _popupRect = rect;
        _PopupPublish();
    }

    private void _PopupPublish(byte[]? pixels = null, int width = 0, int height = 0)
    {
        var popup = new BrowserTabPopup(++_popupRevision, _size.Revision, _size.Scale, _popupVisible,
            _popupRect.X, _popupRect.Y, _popupRect.Width, _popupRect.Height);
        _popups.Writer.TryWrite(new PopupPaint(popup, pixels, width, height));
    }

    private void _PopupPaint(nint pixels, int width, int height)
    {
        if (_closing || _popupVisible == false || width != (int)Math.Ceiling(_popupRect.Width * _size.Scale) ||
            height != (int)Math.Ceiling(_popupRect.Height * _size.Scale))
        {
            return;
        }
        var descriptor = new BrowserIpcFrameDescriptor(Guid.NewGuid(), Guid.NewGuid(), width, height, _id);
        var copy = new byte[checked((int)descriptor.ByteLength)];
        Marshal.Copy(pixels, copy, 0, copy.Length);
        _PopupPublish(copy, width, height);
    }

    private async Task _SendPopupsAsync()
    {
        try
        {
            await foreach (var paint in _popups.Reader.ReadAllAsync())
            {
                using var buffer = paint.Pixels is null ? null : _connection.CreateFrameBuffer(_storage, paint.Width, paint.Height, _id);
                if (buffer is not null && paint.Pixels is { } pixels)
                {
                    buffer.View.WriteArray(0, pixels, 0, pixels.Length);
                }
                await _CallAsync("tab.popup", JsonSerializer.SerializeToElement(paint.Popup with { Buffer = buffer?.Descriptor }));
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            _connection.Close();
        }
    }

    private sealed record PopupPaint(BrowserTabPopup Popup, byte[]? Pixels, int Width, int Height);
}
