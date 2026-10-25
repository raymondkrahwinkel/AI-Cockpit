using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Xilium.CefGlue;

namespace Cockpit.Plugin.Browser.UI.Engine;

// One CEF browser in offscreen rendering (AC-1709 route c). Moving it between tab, rail and window is moving an
// ordinary control: the browser, its page and its sound stay. CEF callbacks arrive on CEF's own UI thread and are
// posted to Avalonia's before anything here or in the model changes.
internal sealed class CefBrowserView : Control
{
    // What the page reports through the console: a playing, audible element, or a format this CEF cannot play.
    internal const string MarkerPrefix = "__cockpit:";

    private const string PageHooks = """
        (()=>{if(window.__cockpitHooks)return;window.__cockpitHooks=1;
        const say=m=>console.log('__cockpit:'+m);
        const audible=()=>{let a=false;document.querySelectorAll('video,audio').forEach(e=>{if(!e.paused&&!e.muted&&e.volume>0)a=true;});say('audible:'+(a?1:0));};
        ['play','pause','ended','volumechange','emptied'].forEach(t=>window.addEventListener(t,audible,true));
        window.addEventListener('error',e=>{const t=e.target;const m=t&&t.tagName==='SOURCE'?t.parentElement:t;if(m&&(m.tagName==='VIDEO'||m.tagName==='AUDIO')&&m.error&&m.error.code===4)say('codec');},true);
        const r=navigator.requestMediaKeySystemAccess;if(r){navigator.requestMediaKeySystemAccess=function(){return r.apply(this,arguments).catch(x=>{say('codec');throw x;});};}
        })();
        """;

    private readonly object _gate = new();
    private readonly BrowserEngine _engine;
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WriteableBitmap? _bitmap;
    private byte[] _back = [];
    private byte[] _front = [];
    private PixelSize _backSize;
    private int _blitQueued;
    private Size _viewSize = new(800, 600);
    private double _scale = 1;
    private volatile CefBrowser? _browser;
    private string? _pendingUrl;
    private volatile bool _closeRequested;

    public CefBrowserView(BrowserEngine engine, string url, CefRequestContext profile)
    {
        _engine = engine;
        Focusable = true;
        ClipToBounds = true;
        engine.Register(this);

        var info = CefWindowInfo.Create();
        info.SetAsWindowless(nint.Zero, true);
        CefBrowserHost.CreateBrowser(info, new Client(this), new CefBrowserSettings { WindowlessFrameRate = 60 }, url, null, profile);
    }

    // A headless scene's page: CEF does not run there, so the page area says so instead of painting.
    private CefBrowserView(BrowserEngine engine)
    {
        _engine = engine;
        IsScenePlaceholder = true;
    }

    internal static CefBrowserView ForScene(BrowserEngine engine) => new(engine);

    internal bool IsScenePlaceholder { get; }

    public event Action<string>? AddressChanged;

    public event Action<string>? TitleChanged;

    public event Action<string>? FaviconChanged;

    public event Action<bool, bool, bool>? LoadingChanged;

    public event Action<bool>? AudibleChanged;

    public event Action? CodecUnsupported;

    public event Action<string>? PopupRequested;

    public bool IsMuted { get; private set; }

    public void Navigate(string url)
    {
        if (_browser is { } browser)
        {
            browser.GetMainFrame().LoadUrl(url);
            return;
        }

        _pendingUrl = url;
    }

    public void GoBack() => _browser?.GoBack();

    public void GoForward() => _browser?.GoForward();

    public void Reload() => _browser?.Reload();

    public void SetMuted(bool muted)
    {
        IsMuted = muted;
        _browser?.GetHost().SetAudioMuted(muted);
    }

    // Closing before CEF created the browser is answered in OnAfterCreated, so no browser is left without an owner.
    public Task CloseAsync()
    {
        _engine.Unregister(this);
        _closeRequested = true;
        if (IsScenePlaceholder)
        {
            return Task.CompletedTask;
        }

        _browser?.GetHost().CloseBrowser(true);
        _engine.Closing(_closed.Task);
        return _closed.Task;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        _OnHost(host =>
        {
            host.WasHidden(false);
            host.NotifyScreenInfoChanged();
            host.WasResized();
        });
    }

    // Hidden, not closed: a page that plays keeps playing (Chromium's background-tab rules), it only stops painting.
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _OnHost(host => host.WasHidden(true));
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        _viewSize = new Size(Math.Max(1, e.NewSize.Width), Math.Max(1, e.NewSize.Height));
        _OnHost(host => host.WasResized());
    }

    public override void Render(DrawingContext context)
    {
        if (IsScenePlaceholder)
        {
            context.FillRectangle(new SolidColorBrush(Color.Parse("#1B1D24")), new Rect(Bounds.Size));
            var text = new FormattedText("Page placeholder · CEF does not run in a headless scene", System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Typeface.Default, 13, new SolidColorBrush(Color.Parse("#8A90A0")));
            context.DrawText(text, new Point((Bounds.Width - text.Width) / 2, (Bounds.Height - text.Height) / 2));
            return;
        }

        context.FillRectangle(Brushes.White, new Rect(Bounds.Size));
        if (_bitmap is not null)
        {
            context.DrawImage(_bitmap, new Rect(_bitmap.Size));
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var mouse = _Mouse(e);
        _OnHost(host => host.SendMouseMoveEvent(mouse, false));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        var mouse = _Mouse(e);
        _OnHost(host => host.SendMouseMoveEvent(mouse, true));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var mouse = _Mouse(e);
        var button = _Button(e.GetCurrentPoint(this).Properties.PointerUpdateKind);
        var clicks = e.ClickCount;
        _OnHost(host => host.SendMouseClickEvent(mouse, button, false, clicks));
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var mouse = _Mouse(e);
        var button = _Button(e.GetCurrentPoint(this).Properties.PointerUpdateKind);
        _OnHost(host => host.SendMouseClickEvent(mouse, button, true, 1));
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var mouse = _Mouse(e);
        var (x, y) = ((int)(e.Delta.X * 120), (int)(e.Delta.Y * 120));
        _OnHost(host => host.SendMouseWheelEvent(mouse, x, y));
        e.Handled = true;
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        _OnHost(host => host.SetFocus(true));
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _OnHost(host => host.SetFocus(false));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        _SendKey(e, CefKeyEventType.RawKeyDown);
        e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        _SendKey(e, CefKeyEventType.KeyUp);
        e.Handled = true;
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        foreach (var character in e.Text ?? string.Empty)
        {
            var key = new CefKeyEvent { EventType = CefKeyEventType.Char, Character = character, UnmodifiedCharacter = character, WindowsKeyCode = character };
            _OnHost(host => host.SendKeyEvent(key));
        }

        e.Handled = true;
    }

    private void _SendKey(KeyEventArgs e, CefKeyEventType type)
    {
        var code = KeyCodes.ToWindows(e.Key);
        if (code == 0)
        {
            return;
        }

        var key = new CefKeyEvent { EventType = type, WindowsKeyCode = code, Modifiers = _Modifiers(e.KeyModifiers) };
        _OnHost(host => host.SendKeyEvent(key));
    }

    private CefMouseEvent _Mouse(PointerEventArgs e)
    {
        var point = e.GetPosition(this);
        var properties = e.GetCurrentPoint(this).Properties;
        var flags = _Modifiers(e.KeyModifiers);
        if (properties.IsLeftButtonPressed)
        {
            flags |= CefEventFlags.LeftMouseButton;
        }

        if (properties.IsRightButtonPressed)
        {
            flags |= CefEventFlags.RightMouseButton;
        }

        if (properties.IsMiddleButtonPressed)
        {
            flags |= CefEventFlags.MiddleMouseButton;
        }

        return new CefMouseEvent((int)point.X, (int)point.Y, flags);
    }

    private static CefMouseButtonType _Button(PointerUpdateKind kind) => kind switch
    {
        PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => CefMouseButtonType.Right,
        PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => CefMouseButtonType.Middle,
        _ => CefMouseButtonType.Left,
    };

    private static CefEventFlags _Modifiers(KeyModifiers modifiers)
    {
        var flags = CefEventFlags.None;
        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            flags |= CefEventFlags.ShiftDown;
        }

        if (modifiers.HasFlag(KeyModifiers.Control))
        {
            flags |= CefEventFlags.ControlDown;
        }

        if (modifiers.HasFlag(KeyModifiers.Alt))
        {
            flags |= CefEventFlags.AltDown;
        }

        return flags;
    }

    // Input and host calls go through CEF's UI thread, the thread CEF documents them on.
    private void _OnHost(Action<CefBrowserHost> action)
    {
        if (_browser is not { } browser)
        {
            return;
        }

        CefRuntime.PostTask(CefThreadId.UI, new ActionTask(() => action(browser.GetHost())));
    }

    private static void _OnUi(Action action) => Dispatcher.UIThread.Post(action);

    // CEF UI thread: copy into our own back buffer and return at once. Locking the WriteableBitmap here waits on
    // Avalonia's compositor and froze the CEF UI thread after two frames (AC-1709).
    private void _Paint(nint buffer, int width, int height)
    {
        lock (_gate)
        {
            var length = width * height * 4;
            if (_back.Length != length)
            {
                _back = new byte[length];
            }

            Marshal.Copy(buffer, _back, 0, length);
            _backSize = new PixelSize(width, height);
        }

        if (Interlocked.Exchange(ref _blitQueued, 1) == 0)
        {
            Dispatcher.UIThread.Post(_Blit, DispatcherPriority.Render);
        }
    }

    // Avalonia UI thread: swap the buffers under the gate, then lock and fill the WriteableBitmap outside it, so
    // OnPaint never waits on a compositor-bound lock (Codex review AC-1711).
    private void _Blit()
    {
        Interlocked.Exchange(ref _blitQueued, 0);
        PixelSize size;
        lock (_gate)
        {
            (_front, _back) = (_back, _front);
            size = _backSize;
        }

        if (size.Width == 0 || _front.Length < size.Width * size.Height * 4)
        {
            return;
        }

        if (_bitmap is null || _bitmap.PixelSize != size)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(size, new Vector(96 * _scale, 96 * _scale), PixelFormat.Bgra8888, AlphaFormat.Premul);
        }

        using (var frame = _bitmap.Lock())
        {
            Marshal.Copy(_front, 0, frame.Address, Math.Min(size.Width * size.Height * 4, frame.RowBytes * size.Height));
        }

        InvalidateVisual();
    }

    private void _OnConsole(string message)
    {
        var marker = message[MarkerPrefix.Length..];
        _OnUi(() =>
        {
            if (marker == "codec")
            {
                CodecUnsupported?.Invoke();
            }
            else if (marker.StartsWith("audible:", StringComparison.Ordinal))
            {
                AudibleChanged?.Invoke(marker.EndsWith('1'));
            }
        });
    }

    private sealed class ActionTask(Action action) : CefTask
    {
        protected override void Execute() => action();
    }

    private sealed class Client(CefBrowserView owner) : CefClient
    {
        private readonly RenderHandler _render = new(owner);
        private readonly LifeSpanHandler _lifeSpan = new(owner);
        private readonly DisplayHandler _display = new(owner);
        private readonly LoadHandler _load = new(owner);

        protected override CefRenderHandler GetRenderHandler() => _render;

        protected override CefLifeSpanHandler GetLifeSpanHandler() => _lifeSpan;

        protected override CefDisplayHandler GetDisplayHandler() => _display;

        protected override CefLoadHandler GetLoadHandler() => _load;
    }

    private sealed class LifeSpanHandler(CefBrowserView owner) : CefLifeSpanHandler
    {
        protected override void OnAfterCreated(CefBrowser browser)
        {
            owner._browser = browser;
            var host = browser.GetHost();
            // Posted, not called: closing inside OnAfterCreated crashed libcef (access violation, measured).
            if (owner._closeRequested)
            {
                CefRuntime.PostTask(CefThreadId.UI, new ActionTask(() => host.CloseBrowser(true)));
                return;
            }

            host.SetAudioMuted(owner.IsMuted);
            host.WasResized();
            host.Invalidate(CefPaintElementType.View);
            if (Interlocked.Exchange(ref owner._pendingUrl, null) is { } url)
            {
                browser.GetMainFrame().LoadUrl(url);
            }
        }

        // A new window becomes a new tab; CEF never opens a window of its own.
        protected override bool OnBeforePopup(CefBrowser browser, CefFrame frame, int popupId, string targetUrl, string targetFrameName,
            CefWindowOpenDisposition targetDisposition, bool userGesture, CefPopupFeatures popupFeatures, CefWindowInfo windowInfo,
            ref CefClient client, CefBrowserSettings settings, ref CefDictionaryValue extraInfo, ref bool noJavascriptAccess)
        {
            _OnUi(() => owner.PopupRequested?.Invoke(targetUrl));
            return true;
        }

        protected override void OnBeforeClose(CefBrowser browser)
        {
            owner._browser = null;
            owner._closed.TrySetResult();
        }
    }

    private sealed class DisplayHandler(CefBrowserView owner) : CefDisplayHandler
    {
        protected override void OnAddressChange(CefBrowser browser, CefFrame frame, string url)
        {
            if (frame.IsMain)
            {
                _OnUi(() => owner.AddressChanged?.Invoke(url));
            }
        }

        protected override void OnTitleChange(CefBrowser browser, string title) => _OnUi(() => owner.TitleChanged?.Invoke(title));

        protected override void OnFaviconUrlChange(CefBrowser browser, string[] iconUrls)
        {
            if (iconUrls.Length > 0)
            {
                _OnUi(() => owner.FaviconChanged?.Invoke(iconUrls[0]));
            }
        }

        protected override bool OnConsoleMessage(CefBrowser browser, CefLogSeverity level, string message, string source, int line)
        {
            if (message.StartsWith(MarkerPrefix, StringComparison.Ordinal) == false)
            {
                return false;
            }

            owner._OnConsole(message);
            return true;
        }

        protected override bool OnCursorChange(CefBrowser browser, nint cursorHandle, CefCursorType type, CefCursorInfo customCursorInfo)
        {
            var cursor = type switch
            {
                CefCursorType.Hand => StandardCursorType.Hand,
                CefCursorType.IBeam => StandardCursorType.Ibeam,
                CefCursorType.Wait => StandardCursorType.Wait,
                _ => StandardCursorType.Arrow,
            };
            _OnUi(() => owner.Cursor = new Cursor(cursor));
            return true;
        }
    }

    private sealed class LoadHandler(CefBrowserView owner) : CefLoadHandler
    {
        protected override void OnLoadingStateChange(CefBrowser browser, bool isLoading, bool canGoBack, bool canGoForward) =>
            _OnUi(() => owner.LoadingChanged?.Invoke(isLoading, canGoBack, canGoForward));

        // Before the page's own scripts, so a media error or a DRM request that comes early is still seen.
        protected override void OnLoadStart(CefBrowser browser, CefFrame frame, CefTransitionType transitionType)
        {
            if (frame.IsMain)
            {
                frame.ExecuteJavaScript(PageHooks, frame.Url, 0);
                _OnUi(() => owner.AudibleChanged?.Invoke(false));
            }
        }
    }

    private sealed class NoAccessibility : CefAccessibilityHandler
    {
        protected override void OnAccessibilityTreeChange(CefValue value)
        {
        }

        protected override void OnAccessibilityLocationChange(CefValue value)
        {
        }
    }

    private sealed class RenderHandler(CefBrowserView owner) : CefRenderHandler
    {
        private readonly NoAccessibility _accessibility = new();

        protected override CefAccessibilityHandler GetAccessibilityHandler() => _accessibility;

        protected override void GetViewRect(CefBrowser browser, out CefRectangle rect) =>
            rect = new CefRectangle(0, 0, (int)owner._viewSize.Width, (int)owner._viewSize.Height);

        protected override bool GetScreenInfo(CefBrowser browser, CefScreenInfo screenInfo)
        {
            screenInfo.DeviceScaleFactor = (float)owner._scale;
            var rect = new CefRectangle(0, 0, (int)owner._viewSize.Width, (int)owner._viewSize.Height);
            screenInfo.Rectangle = rect;
            screenInfo.AvailableRectangle = rect;
            return true;
        }

        protected override void OnPaint(CefBrowser browser, CefPaintElementType type, CefRectangle[] dirtyRects, nint buffer, int width, int height)
        {
            if (type == CefPaintElementType.View)
            {
                owner._Paint(buffer, width, height);
            }
        }

        protected override void OnAcceleratedPaint(CefBrowser browser, CefPaintElementType type, CefRectangle[] dirtyRects, CefAcceleratedPaintInfo info)
        {
        }

        protected override void OnScrollOffsetChanged(CefBrowser browser, double x, double y)
        {
        }

        protected override void OnImeCompositionRangeChanged(CefBrowser browser, CefRange selectedRange, CefRectangle[] characterBounds)
        {
        }

        protected override void OnPopupSize(CefBrowser browser, CefRectangle rect)
        {
        }
    }
}
