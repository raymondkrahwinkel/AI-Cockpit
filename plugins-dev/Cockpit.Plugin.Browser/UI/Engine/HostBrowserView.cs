using System.Text.Json;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Cockpit.Plugin.Browser.Ipc;

namespace Cockpit.Plugin.Browser.UI.Engine;

internal sealed class HostBrowserView : BrowserView
{
    private readonly BrowserHostClient _host;
    private readonly Channel<(string Operation, JsonElement Payload)> _commands = Channel.CreateBounded<(string, JsonElement)>(64);
    private readonly Task _sender;
    private readonly Task _disconnected;
    private BrowserIpcFrameBuffer? _buffer;
    private WriteableBitmap? _bitmap;
    private BrowserTabSize _size = new(800, 600, 1, 1);
    private long _frameId;
    private long _bufferRevision;
    private bool _closing;
    private bool _muted;
    private bool _faulted;
    private Task? _close;
    private Vector _wheelRest;

    internal HostBrowserView(BrowserHostClient host, string address)
    {
        _host = host;
        Focusable = true;
        ClipToBounds = true;
        host.RegisterTab(TabId, _HandleAsync);
        _sender = _SendAsync(address);
        _disconnected = _DisconnectedAsync();
    }

    internal override Guid HostGeneration => _host.Generation;
    internal string? Failure { get; private set; }
    internal event Action<BrowserTabFrame, WriteableBitmap>? FramePresented;
    public override bool IsMuted => _muted;
    public override void SetMuted(bool muted) => _muted = muted;
    public override void Navigate(string url) => _Queue("tab.navigate", url);
    public override void GoBack() => _Queue("tab.back", true);
    public override void GoForward() => _Queue("tab.forward", true);
    public override void Reload() => _Queue("tab.reload", true);

    private void _Queue<T>(string operation, T payload)
    {
        if (_closing || _faulted)
        {
            return;
        }
        if (_commands.Writer.TryWrite((operation, JsonSerializer.SerializeToElement(payload))) == false)
        {
            _Fail("The browser input queue is full. Close this tab and open it again.");
        }
    }

    private async Task _SendAsync(string address)
    {
        try
        {
            await _host.Connection.CallAsync(_host.Generation, "enginehost", "tab.create",
                JsonSerializer.SerializeToElement(new BrowserTabCreate(TabId, address)), BrowserIpcLimits.Deadline).ConfigureAwait(false);
            await foreach (var command in _commands.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (_closing)
                {
                    break;
                }
                await _host.Connection.CallAsync(TabId, "operator", command.Operation, command.Payload, BrowserIpcLimits.Deadline).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            await Dispatcher.UIThread.InvokeAsync(() => _Fail("The browser tab connection stopped. Close this tab and open it again."));
        }
    }

    private async Task<JsonElement> _HandleAsync(BrowserIpcMessage message, CancellationToken token)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            token.ThrowIfCancellationRequested();
            if (_closing)
            {
                return;
            }
            switch (message.Operation)
            {
                case "tab.frame":
                    _Present(message.Payload.Deserialize<BrowserTabFrame>() ?? throw new InvalidDataException("Missing browser frame."));
                    break;
                case "tab.state":
                    var state = message.Payload.Deserialize<BrowserTabState>() ?? throw new InvalidDataException("Missing browser state.");
                    OnAddressChanged(state.Address);
                    OnTitleChanged(state.Title);
                    OnLoadingChanged(state.Loading, state.Back, state.Forward);
                    Failure = state.Failure;
                    InvalidateVisual();
                    break;
                default: throw new InvalidDataException("Unknown browser callback.");
            }
        }, DispatcherPriority.Render, token);
        return JsonSerializer.SerializeToElement(true);
    }

    private void _Present(BrowserTabFrame frame)
    {
        if (frame.Buffer.Generation != HostGeneration || frame.Buffer.TabId != TabId || frame.Revision < 1 || frame.FrameId <= _frameId ||
            frame.Revision > _size.Revision || double.IsFinite(frame.Scale) == false || frame.Scale < 0.5 || frame.Scale > 8)
        {
            throw new InvalidDataException("Browser frame correlation failed.");
        }
        _frameId = frame.FrameId;
        if (frame.Revision < _size.Revision)
        {
            return;
        }
        if (frame.Buffer.Width != (int)Math.Ceiling(_size.Width * _size.Scale) || frame.Buffer.Height != (int)Math.Ceiling(_size.Height * _size.Scale) ||
            frame.Scale != _size.Scale || (_bufferRevision == frame.Revision && _buffer?.Descriptor != frame.Buffer))
        {
            throw new InvalidDataException("Browser frame size or buffer changed without a resize.");
        }
        if (_bufferRevision != frame.Revision)
        {
            if (frame.X != 0 || frame.Y != 0 || frame.Width != frame.Buffer.Width || frame.Height != frame.Buffer.Height)
            {
                throw new InvalidDataException("The first browser frame must fill the buffer.");
            }
            _buffer?.Dispose();
            _buffer = _host.Connection.OpenFrameBuffer(_host.BufferDirectory, frame.Buffer, TabId);
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(new PixelSize(frame.Buffer.Width, frame.Buffer.Height), new Vector(96 * frame.Scale, 96 * frame.Scale),
                PixelFormat.Bgra8888, AlphaFormat.Premul);
            _bufferRevision = frame.Revision;
        }
        if (_bitmap is { } bitmap && _buffer is { } buffer)
        {
            using (var target = bitmap.Lock())
            {
                buffer.CopyRectangle(target.Address, target.RowBytes, frame.X, frame.Y, frame.Width, frame.Height, write: false);
            }
            FramePresented?.Invoke(frame, bitmap);
            InvalidateVisual();
        }
    }

    private async Task _DisconnectedAsync()
    {
        await _host.Connection.Completion.ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_closing == false)
            {
                _bitmap?.Dispose();
                _bitmap = null;
                _Fail("The browser engine stopped. Close this tab and open it again.");
            }
        });
    }

    private void _Fail(string reason)
    {
        _faulted = true;
        Failure = reason;
        _commands.Writer.TryComplete();
        OnTitleChanged("Browser stopped");
        OnLoadingChanged(false, false, false);
        InvalidateVisual();
    }

    public override Task CloseAsync()
    {
        _closing = true;
        _buffer?.Dispose();
        _buffer = null;
        _commands.Writer.TryComplete();
        return _close ??= _CloseAsync();
    }

    private async Task _CloseAsync()
    {
        try
        {
            await _sender;
            await _host.Connection.CallAsync(_host.Generation, "enginehost", "tab.close", JsonSerializer.SerializeToElement(TabId), BrowserIpcLimits.Deadline);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            _host.Connection.Close();
            await _disconnected.WaitAsync(BrowserIpcLimits.Deadline);
        }
        finally
        {
            await _host.UnregisterTabAsync(TabId);
            _buffer?.Dispose();
            _buffer = null;
            _bitmap?.Dispose();
            _bitmap = null;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _Resize(Bounds.Size);
        _Queue("tab.visible", true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _Queue("tab.visible", false);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        _Resize(e.NewSize);
    }

    private void _Resize(Size size)
    {
        int width = Math.Clamp((int)Math.Ceiling(size.Width), 1, 8192);
        int height = Math.Clamp((int)Math.Ceiling(size.Height), 1, 8192);
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        if (_size.Width != width || _size.Height != height || _size.Scale != scale)
        {
            _size = new BrowserTabSize(width, height, scale, checked(_size.Revision + 1));
            _buffer?.Dispose();
            _buffer = null;
            _Queue("tab.size", _size);
        }
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.White, new Rect(Bounds.Size));
        if (Failure is { } reason)
        {
            var text = new FormattedText(reason, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 13, Brushes.Black);
            context.DrawText(text, new Point(12, 12));
        }
        else if (_bitmap is { } bitmap)
        {
            context.DrawImage(bitmap, new Rect(bitmap.PixelSize.ToSize(1)), new Rect(bitmap.Size));
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _Mouse(e, "move");
    }
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _Mouse(e, "move", up: true);
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        OnOperatorInput();
        Focus();
        e.Pointer.Capture(this);
        _Mouse(e, "click", count: e.ClickCount);
        e.Handled = true;
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
        _Mouse(e, "click", up: true);
        e.Handled = true;
    }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        OnOperatorInput();
        var delta = e.Delta * 120 + _wheelRest;
        _wheelRest = new Vector(delta.X - (int)delta.X, delta.Y - (int)delta.Y);
        _Mouse(e, "wheel", deltaX: (int)delta.X, deltaY: (int)delta.Y);
        e.Handled = true;
    }
    private void _Mouse(PointerEventArgs e, string kind, bool up = false, int count = 1, int deltaX = 0, int deltaY = 0)
    {
        var point = e.GetPosition(this);
        var properties = e.GetCurrentPoint(this).Properties;
        int flags = _Modifiers(e.KeyModifiers) | (properties.IsLeftButtonPressed ? 16 : 0) | (properties.IsMiddleButtonPressed ? 32 : 0) | (properties.IsRightButtonPressed ? 64 : 0);
        int button = properties.PointerUpdateKind switch
        {
            PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => 1,
            PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => 2,
            _ => 0,
        };
        _Queue("tab.input", new BrowserTabInput(kind, (int)point.X, (int)point.Y, flags, button, count, deltaX, deltaY, Up: up));
    }
    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        _Queue("tab.input", new BrowserTabInput("focus"));
    }
    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _Queue("tab.input", new BrowserTabInput("focus", Up: true));
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        OnOperatorInput();
        _Queue("tab.input", new BrowserTabInput("key", Flags: _Modifiers(e.KeyModifiers), Key: KeyCodes.ToWindows(e.Key)));
        if (e.Key == Key.Enter)
        {
            _Queue("tab.input", new BrowserTabInput("key", Flags: _Modifiers(e.KeyModifiers), Key: 13, Character: 13));
        }
        e.Handled = e.KeySymbol is not { Length: > 0 } symbol || char.IsControl(symbol[0]);
    }
    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        _Queue("tab.input", new BrowserTabInput("key", Flags: _Modifiers(e.KeyModifiers), Key: KeyCodes.ToWindows(e.Key), Up: true));
        e.Handled = true;
    }
    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        foreach (char character in e.Text ?? string.Empty)
        {
            _Queue("tab.input", new BrowserTabInput("key", Key: character, Character: character));
        }
        e.Handled = true;
    }
    private static int _Modifiers(KeyModifiers modifiers) => (modifiers.HasFlag(KeyModifiers.Shift) ? 2 : 0)
        | (modifiers.HasFlag(KeyModifiers.Control) ? 4 : 0) | (modifiers.HasFlag(KeyModifiers.Alt) ? 8 : 0);
}
