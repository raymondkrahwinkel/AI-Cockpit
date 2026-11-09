using System.Text.Json;
using System.Threading.Channels;
using Cockpit.Plugin.Browser.Ipc;
using Xilium.CefGlue;

namespace Cockpit.Plugin.Browser.Host;

internal sealed class BrowserHostTab : IAsyncDisposable
{
    private readonly BrowserIpcConnection _connection;
    private readonly string _storage;
    private readonly Guid _id;
    private readonly Channel<BrowserTabState> _states = Channel.CreateBounded<BrowserTabState>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Task _stateSender;
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly TaskCompletionSource _created = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private BrowserTabState _state;
    private BrowserTabSize _size = new(800, 600, 1, 1);
    private CefBrowser? _browser;
    private BrowserIpcFrameBuffer? _buffer;
    private CefRectangle _dirty;
    private bool _inflight;
    private bool _closing;
    private bool _creationPending;
    private long _frameId;
    private long _bufferRevision;
    private Task _frameSender = Task.CompletedTask;

    internal BrowserHostTab(BrowserIpcConnection connection, string storage, Guid id, string url)
    {
        _connection = connection;
        _storage = storage;
        _id = id;
        _state = new BrowserTabState(url, "New tab", true, false, false);
        _stateSender = _SendStatesAsync();
    }

    internal Task Created => _created.Task;

    internal void Create(CefRequestContext profile, BrowserTabSize size)
    {
        _ValidateSize(size);
        _size = size;
        var info = CefWindowInfo.Create();
        info.SetAsWindowless(nint.Zero, true);
        _creationPending = CefBrowserHost.CreateBrowser(info, new Client(this), new CefBrowserSettings { WindowlessFrameRate = 60 }, _state.Address, null, profile);
        if (_creationPending == false)
        {
            throw new InvalidOperationException("CEF refused the browser tab.");
        }
    }

    internal Task HandleAsync(BrowserIpcMessage message, CancellationToken token) => OnCefAsync(() =>
    {
        if (_browser is not { } browser || _closing)
        {
            throw new InvalidDataException("The browser tab is closed.");
        }
        var host = browser.GetHost();
        switch (message.Operation)
        {
            case "tab.navigate":
                string url = message.Payload.GetString() ?? string.Empty;
                if (Uri.TryCreate(url, UriKind.Absolute, out _) == false)
                {
                    throw new InvalidDataException("Invalid browser address.");
                }
                browser.GetMainFrame().LoadUrl(url);
                break;
            case "tab.back": browser.GoBack(); break;
            case "tab.forward": browser.GoForward(); break;
            case "tab.reload": browser.Reload(); break;
            case "tab.size":
                var size = message.Payload.Deserialize<BrowserTabSize>() ?? throw new InvalidDataException("Missing browser size.");
                _ValidateSize(size);
                if (size.Revision <= _size.Revision)
                {
                    throw new InvalidDataException("Stale browser size.");
                }
                _size = size;
                _dirty = default;
                host.NotifyScreenInfoChanged();
                host.WasResized();
                break;
            case "tab.visible":
                host.WasHidden(message.Payload.GetBoolean() == false);
                break;
            case "tab.input":
                _Input(host, message.Payload.Deserialize<BrowserTabInput>() ?? throw new InvalidDataException("Missing browser input."));
                break;
            default: throw new InvalidDataException("Unknown browser tab operation.");
        }
    }, token);

    private static void _ValidateSize(BrowserTabSize size)
    {
        if (size.Width < 1 || size.Height < 1 || double.IsFinite(size.Scale) == false || size.Scale < 0.5 || size.Scale > 8 || size.Revision < 1)
        {
            throw new InvalidDataException("Invalid browser size.");
        }
        _ = new BrowserIpcFrameDescriptor(Guid.NewGuid(), Guid.NewGuid(), checked((int)Math.Ceiling(size.Width * size.Scale)),
            checked((int)Math.Ceiling(size.Height * size.Scale))).ByteLength;
    }

    private static void _Input(CefBrowserHost host, BrowserTabInput input)
    {
        var mouse = new CefMouseEvent(input.X, input.Y, (CefEventFlags)input.Flags);
        switch (input.Kind)
        {
            case "move": host.SendMouseMoveEvent(mouse, input.Up); break;
            case "click" when Enum.IsDefined((CefMouseButtonType)input.Button) && input.Count is > 0 and <= 3:
                host.SetFocus(true);
                host.SendMouseClickEvent(mouse, (CefMouseButtonType)input.Button, input.Up, input.Count);
                break;
            case "wheel": host.SendMouseWheelEvent(mouse, input.DeltaX, input.DeltaY); break;
            case "focus": host.SetFocus(input.Up == false); break;
            case "key" when input.Character is >= 0 and <= char.MaxValue:
                host.SendKeyEvent(new CefKeyEvent
                {
                    EventType = input.Character != 0 ? CefKeyEventType.Char : input.Up ? CefKeyEventType.KeyUp : CefKeyEventType.RawKeyDown,
                    WindowsKeyCode = input.Key, Character = (char)input.Character, UnmodifiedCharacter = (char)input.Character,
                    Modifiers = (CefEventFlags)input.Flags,
                });
                break;
            default: throw new InvalidDataException("Invalid browser input.");
        }
    }

    private void _Paint(nint pixels, int width, int height, CefRectangle[] rectangles)
    {
        long frameId = ++_frameId;
        if (_closing || width != (int)Math.Ceiling(_size.Width * _size.Scale) || height != (int)Math.Ceiling(_size.Height * _size.Scale))
        {
            return;
        }
        foreach (var rectangle in rectangles)
        {
            _dirty = _Union(_dirty, rectangle);
        }
        if (_inflight)
        {
            return;
        }
        if (_bufferRevision != _size.Revision)
        {
            _buffer?.Dispose();
            _buffer = _connection.CreateFrameBuffer(_storage, width, height, _id);
            _bufferRevision = _size.Revision;
            _dirty = new CefRectangle(0, 0, width, height);
        }
        if (_buffer is not { } buffer || _dirty.Width == 0 || _dirty.Height == 0)
        {
            return;
        }
        var dirty = _dirty;
        buffer.CopyRectangle(pixels, width * 4, dirty.X, dirty.Y, dirty.Width, dirty.Height, write: true);
        _dirty = default;
        _inflight = true;
        var frame = new BrowserTabFrame(buffer.Descriptor, _size.Revision, frameId, dirty.X, dirty.Y, dirty.Width, dirty.Height, _size.Scale);
        _frameSender = _SendFrameAsync(frame);
    }

    private async Task _SendFrameAsync(BrowserTabFrame frame)
    {
        try
        {
            await _CallAsync("tab.frame", JsonSerializer.SerializeToElement(frame));
            await OnCefAsync(() =>
            {
                _inflight = false;
                if (_closing == false && (_dirty.Width > 0 || _bufferRevision != _size.Revision))
                {
                    _browser?.GetHost().Invalidate(CefPaintElementType.View);
                }
            }, CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            _connection.Close();
        }
    }

    private async Task _SendStatesAsync()
    {
        try
        {
            await foreach (var state in _states.Reader.ReadAllAsync())
            {
                await _CallAsync("tab.state", JsonSerializer.SerializeToElement(state));
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            _connection.Close();
        }
    }

    private void _State(BrowserTabState state)
    {
        _state = state;
        _states.Writer.TryWrite(state);
    }

    private async Task _CallAsync(string operation, JsonElement payload)
    {
        await _send.WaitAsync();
        try
        {
            await _connection.CallAsync(_id, "operator", operation, payload, BrowserIpcLimits.Deadline);
        }
        finally
        {
            _send.Release();
        }
    }

    private static CefRectangle _Union(CefRectangle first, CefRectangle second)
    {
        if (first.Width == 0 || first.Height == 0)
        {
            return second;
        }
        int x = Math.Min(first.X, second.X);
        int y = Math.Min(first.Y, second.Y);
        return new CefRectangle(x, y, Math.Max(first.X + first.Width, second.X + second.Width) - x,
            Math.Max(first.Y + first.Height, second.Y + second.Height) - y);
    }

    internal static Task OnCefAsync(Action action, CancellationToken token)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CefRuntime.PostTask(CefThreadId.UI, new ActionTask(() =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                action();
                done.TrySetResult();
            }
            catch (Exception exception)
            {
                done.TrySetException(exception);
            }
        }));
        return done.Task;
    }

    public async ValueTask DisposeAsync()
    {
        await OnCefAsync(() =>
        {
            _closing = true;
            if (_browser is { } browser)
            {
                browser.GetHost().CloseBrowser(true);
            }
            else if (_creationPending == false)
            {
                _closed.TrySetResult();
            }
        }, CancellationToken.None);
        _states.Writer.TryComplete();
        await _closed.Task.WaitAsync(BrowserIpcLimits.Deadline);
        await Task.WhenAll(_frameSender, _stateSender);
        _buffer?.Dispose();
        _send.Dispose();
    }

    private sealed class ActionTask(Action action) : CefTask
    {
        protected override void Execute() => action();
    }

    private sealed class Client(BrowserHostTab owner) : CefClient
    {
        private readonly LifeSpanHandler _life = new(owner);
        private readonly RenderHandler _render = new(owner);
        private readonly DisplayHandler _display = new(owner);
        private readonly LoadHandler _load = new(owner);
        private readonly RequestHandler _request = new(owner);
        protected override CefLifeSpanHandler GetLifeSpanHandler() => _life;
        protected override CefRenderHandler GetRenderHandler() => _render;
        protected override CefDisplayHandler GetDisplayHandler() => _display;
        protected override CefLoadHandler GetLoadHandler() => _load;
        protected override CefRequestHandler GetRequestHandler() => _request;
    }

    private sealed class LifeSpanHandler(BrowserHostTab owner) : CefLifeSpanHandler
    {
        protected override void OnAfterCreated(CefBrowser browser)
        {
            owner._creationPending = false;
            owner._browser = browser;
            owner._created.TrySetResult();
            if (owner._closing)
            {
                CefRuntime.PostTask(CefThreadId.UI, new ActionTask(() => browser.GetHost().CloseBrowser(true)));
                return;
            }
            browser.GetHost().WasResized();
        }
        protected override void OnBeforeClose(CefBrowser browser)
        {
            owner._browser = null;
            owner._closed.TrySetResult();
        }
        protected override bool OnBeforePopup(CefBrowser browser, CefFrame frame, int popupId, string targetUrl, string targetFrameName,
            CefWindowOpenDisposition targetDisposition, bool userGesture, CefPopupFeatures popupFeatures, CefWindowInfo windowInfo,
            ref CefClient client, CefBrowserSettings settings, ref CefDictionaryValue extraInfo, ref bool noJavascriptAccess) => true;
    }

    private sealed class DisplayHandler(BrowserHostTab owner) : CefDisplayHandler
    {
        protected override void OnAddressChange(CefBrowser browser, CefFrame frame, string url)
        {
            if (frame.IsMain)
            {
                owner._State(owner._state with { Address = url });
            }
        }
        protected override void OnTitleChange(CefBrowser browser, string title) => owner._State(owner._state with { Title = title });
    }

    private sealed class LoadHandler(BrowserHostTab owner) : CefLoadHandler
    {
        protected override void OnLoadingStateChange(CefBrowser browser, bool isLoading, bool canGoBack, bool canGoForward)
        {
            if (isLoading == false)
            {
                browser.GetHost().GetRequestContext().GetCookieManager(null)?.FlushStore(null);
            }
            owner._State(owner._state with { Loading = isLoading, Back = canGoBack, Forward = canGoForward, Failure = null });
        }
    }

    private sealed class RequestHandler(BrowserHostTab owner) : CefRequestHandler
    {
        protected override CefResourceRequestHandler? GetResourceRequestHandler(CefBrowser browser, CefFrame frame, CefRequest request,
            bool isNavigation, bool isDownload, string requestInitiator, ref bool disableDefaultHandling) => null;
        protected override void OnRenderProcessTerminated(CefBrowser browser, CefTerminationStatus status, int errorCode, string errorString) =>
            owner._State(owner._state with { Failure = "The page renderer stopped. Reload the tab to try again." });
    }

    private sealed class RenderHandler(BrowserHostTab owner) : CefRenderHandler
    {
        protected override CefAccessibilityHandler? GetAccessibilityHandler() => null;
        protected override void GetViewRect(CefBrowser browser, out CefRectangle rect) =>
            rect = new CefRectangle(0, 0, owner._size.Width, owner._size.Height);
        protected override bool GetScreenInfo(CefBrowser browser, CefScreenInfo screenInfo)
        {
            screenInfo.DeviceScaleFactor = (float)owner._size.Scale;
            screenInfo.Rectangle = new CefRectangle(0, 0, owner._size.Width, owner._size.Height);
            screenInfo.AvailableRectangle = screenInfo.Rectangle;
            return true;
        }
        protected override void OnPaint(CefBrowser browser, CefPaintElementType type, CefRectangle[] dirtyRects, nint buffer, int width, int height)
        {
            if (type == CefPaintElementType.View)
            {
                try
                {
                    owner._Paint(buffer, width, height, dirtyRects);
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException or InvalidOperationException or AggregateException)
                {
                    owner._connection.Close();
                }
            }
        }
        protected override void OnAcceleratedPaint(CefBrowser browser, CefPaintElementType type, CefRectangle[] dirtyRects, CefAcceleratedPaintInfo info) { }
        protected override void OnScrollOffsetChanged(CefBrowser browser, double x, double y) { }
        protected override void OnImeCompositionRangeChanged(CefBrowser browser, CefRange selectedRange, CefRectangle[] characterBounds) { }
        protected override void OnPopupSize(CefBrowser browser, CefRectangle rect) { }
    }
}
