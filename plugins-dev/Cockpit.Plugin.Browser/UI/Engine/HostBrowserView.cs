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

internal sealed partial class HostBrowserView : BrowserView
{
    private readonly BrowserHostClient _host;
    private readonly Channel<Command> _commands = Channel.CreateBounded<Command>(64);
    private readonly object _commandGate = new();
    private Command? _move;
    private Command? _wheel;
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
    private (string? Origin, Guid? Grant) _control;
    private long _controlRevision;
    private long _focusRevision;
    private long _heldId;
    private bool _heldOpen;
    private readonly TaskCompletionSource _created = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _direct;
    private TopLevel? _topLevel;

    internal HostBrowserView(BrowserHostClient host, string address)
    {
        _host = host;
        Focusable = true;
        ClipToBounds = true;
        _ime = new ImeClient(this);
        TextInputMethodClientRequested += (_, e) => e.Client = _ime;
        host.RegisterTab(TabId, _HandleAsync);
        _direct = _created.Task;
        _sender = _SendAsync(address);
        _disconnected = _DisconnectedAsync();
    }

    internal override Guid HostGeneration => _host.Generation;
    internal override CancellationToken HostStopped => _host.Connection.Closed;
    internal string? Failure { get; private set; }
    public override bool IsMuted => _muted;
    public override void SetMuted(bool muted)
    {
        _muted = muted;
        _Queue("tab.mute", muted);
    }
    public override void Navigate(string url) => _Queue("tab.navigate", url);
    public override void GoBack() => _Queue("tab.back", true);
    public override void GoForward() => _Queue("tab.forward", true);
    public override void Reload() => _Queue("tab.reload", true);

    // AC-2148: the host runs calls one at a time, so a page that never answers may hold its queue only this long.
    private static readonly TimeSpan DevToolsDeadline = TimeSpan.FromSeconds(10);

    public override async Task<string?> DevToolsAsync(string method, IReadOnlyDictionary<string, object> parameters) =>
        (await _CallAsync("tab.devtools", new BrowserTabDevTools(method, parameters), DevToolsDeadline))?.GetString();

    // AC-2149: the host decides each action against the control it was last sent, on CEF's thread; this side only
    // skips sending one its own registry already revoked. Nothing here can make the host act without that grant.
    public override Task<bool> AgentClick(int x, int y, AgentGate gate, string frames) =>
        _AgentAsync(gate, new BrowserTabAgent("click", gate.Grant, gate.NeedsFocus, X: x, Y: y, Frames: frames));
    public override Task<bool> AgentType(string text, AgentGate gate) => _AgentAsync(gate, new BrowserTabAgent("type", gate.Grant, gate.NeedsFocus, Text: text));
    public override Task<bool> AgentKey(int code, AgentGate gate) => _AgentAsync(gate, new BrowserTabAgent("key", gate.Grant, gate.NeedsFocus, Key: code));
    public override Task<bool> AgentScroll(int dx, int dy, AgentGate gate) => _AgentAsync(gate, new BrowserTabAgent("scroll", gate.Grant, gate.NeedsFocus, X: dx, Y: dy));
    public override Task<bool> AgentNavigate(string url, AgentGate gate) => _AgentAsync(gate, new BrowserTabAgent("navigate", gate.Grant, gate.NeedsFocus, Text: url));
    public override async Task<string?> FrameSignatureAsync() => (await _CallAsync("tab.frames", true, BrowserIpcLimits.Deadline))?.GetString();

    private async Task<bool> _AgentAsync(AgentGate gate, BrowserTabAgent action) =>
        gate.Stands() && await _CallAsync("tab.agent", action, BrowserIpcLimits.Deadline) is { ValueKind: JsonValueKind.True };

    // Sent beside the input queue so a revocation overtakes queued input, in the order the operator's choices were made.
    public override void SetControl(string? origin, Guid? actingGrant)
    {
        if (_control == (origin, actingGrant))
        {
            return;
        }
        _control = (origin, actingGrant);
        _Direct("tab.control", new BrowserTabControl(origin, actingGrant, ++_controlRevision));
    }

    public override void ReleaseHeldSubmit() => _Release(send: true);
    public override void DropHeldSubmit() => _Release(send: false);

    private void _Release(bool send)
    {
        if (_heldOpen)
        {
            _heldOpen = false;
            _Direct("tab.release", new BrowserTabRelease(_heldId, send));
        }
    }

    private void _Direct<T>(string operation, T payload)
    {
        if (_closing == false && _faulted == false)
        {
            _direct = _DirectAsync(_direct, operation, JsonSerializer.SerializeToElement(payload));
        }
    }

    // A control change that may not have landed must not leave a grant standing: closing the connection ends the host,
    // and with it every grant and held request.
    private async Task _DirectAsync(Task previous, string operation, JsonElement payload)
    {
        await previous;
        if (_closing || _faulted)
        {
            return;
        }
        try
        {
            await _host.Connection.CallAsync(TabId, "operator", operation, payload, BrowserIpcLimits.Deadline).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or OperationCanceledException or ObjectDisposedException or InvalidOperationException or TimeoutException)
        {
            _host.Connection.Close();
        }
    }

    private async Task<JsonElement?> _CallAsync<T>(string operation, T payload, TimeSpan timeout)
    {
        if (_closing || _faulted)
        {
            return null;
        }
        try
        {
            return await _host.Connection.CallAsync(TabId, "operator", operation, JsonSerializer.SerializeToElement(payload), timeout).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or OperationCanceledException or ObjectDisposedException or InvalidOperationException or TimeoutException)
        {
            return null;
        }
    }

    private void _Queue<T>(string operation, T payload)
    {
        if (_closing || _faulted)
        {
            return;
        }
        var command = new Command(operation, JsonSerializer.SerializeToElement(payload),
            payload is BrowserTabInput { Kind: "key" } or BrowserTabIme ? _focusRevision : null);
        lock (_commandGate)
        {
            if (payload is BrowserTabInput { Kind: "move" or "wheel" } pointer)
            {
                ref Command? pending = ref (pointer.Kind == "move" ? ref _move : ref _wheel);
                if (pending is { } queued)
                {
                    if (pointer.Kind == "wheel")
                    {
                        var previous = queued.Payload.Deserialize<BrowserTabInput>() ?? throw new InvalidDataException("Missing queued browser input.");
                        command.Payload = JsonSerializer.SerializeToElement(pointer with { DeltaX = previous.DeltaX + pointer.DeltaX, DeltaY = previous.DeltaY + pointer.DeltaY });
                    }
                    queued.Payload = command.Payload;
                    return;
                }
                pending = command;
            }
            else
            {
                _move = null;
                _wheel = null;
            }
            if (_commands.Writer.TryWrite(command))
            {
                return;
            }
        }
        _Fail("The browser input queue is full. Close this tab and open it again.");
    }

    private async Task _SendAsync(string address)
    {
        try
        {
            try
            {
                await _host.Connection.CallAsync(_host.Generation, "enginehost", "tab.create",
                    JsonSerializer.SerializeToElement(new BrowserTabCreate(TabId, address)), BrowserIpcLimits.Deadline).ConfigureAwait(false);
            }
            finally
            {
                _created.TrySetResult();
            }
            await foreach (var command in _commands.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (_closing)
                {
                    break;
                }
                JsonElement payload;
                lock (_commandGate)
                {
                    if (ReferenceEquals(_move, command))
                    {
                        _move = null;
                    }
                    if (ReferenceEquals(_wheel, command))
                    {
                        _wheel = null;
                    }
                    payload = command.Payload;
                }
                if (command.FocusRevision is { } revision && revision != Volatile.Read(ref _focusRevision))
                {
                    continue;
                }
                await _host.Connection.CallAsync(TabId, "operator", command.Operation, payload, BrowserIpcLimits.Deadline).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or OperationCanceledException or ObjectDisposedException or InvalidOperationException or TimeoutException)
        {
            await Dispatcher.UIThread.InvokeAsync(() => _Fail("The browser tab connection stopped. Close this tab and open it again."));
        }
    }

    private async Task<JsonElement> _HandleAsync(BrowserIpcMessage message, CancellationToken token)
    {
        if (message.Operation == "tab.files")
        {
            var request = message.Payload.Deserialize<BrowserTabFileDialog>() ?? throw new InvalidDataException("Missing file dialog.");
            Task<string[]> selection = Task.FromResult(Array.Empty<string>());
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                selection = _closing || _faulted || _control.Origin is not null ? Task.FromResult(Array.Empty<string>()) : BrowserFilePicker.ChooseAsync(this, request, token);
            }, DispatcherPriority.Normal, token);
            string[] paths = await selection.WaitAsync(token);
            return JsonSerializer.SerializeToElement(_closing || _faulted || _control.Origin is not null ? [] : paths);
        }
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            token.ThrowIfCancellationRequested();
            if (_closing)
            {
                return;
            }
            switch (message.Operation)
            {
                case "tab.newTab":
                    var popup = message.Payload.Deserialize<BrowserTabNewTab>() ?? throw new InvalidDataException("Missing popup address.");
                    if (_faulted == false && _control.Origin is null && popup.ControlRevision == _controlRevision)
                    {
                        OnPopupRequested(popup.Address);
                    }
                    break;
                case "tab.popup":
                    _PresentPopup(message.Payload.Deserialize<BrowserTabPopup>() ?? throw new InvalidDataException("Missing browser popup."));
                    break;
                case "tab.frame":
                    _Present(message.Payload.Deserialize<BrowserTabFrame>() ?? throw new InvalidDataException("Missing browser frame."));
                    break;
                case "tab.state":
                    var state = message.Payload.Deserialize<BrowserTabState>() ?? throw new InvalidDataException("Missing browser state.");
                    OnAddressChanged(state.Address);
                    OnTitleChanged(state.Title);
                    OnLoadingChanged(state.Loading, state.Back, state.Forward);
                    OnAudibleChanged(state.Audible);
                    Failure = state.Failure;
                    if (Failure is not null)
                    {
                        _ClearPopup();
                    }
                    if (state.Ime is { } ime)
                    {
                        _ime.Present(ime);
                    }
                    _heldOpen = state.Held is not null;
                    if (state.Held is { } held && held.Id != _heldId)
                    {
                        _heldId = held.Id;
                        OnSubmitHeld(held.Method, held.Url);
                    }
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
        _ClearPopup();
        _ime.Cancel();
        _faulted = true;
        Failure = reason;
        _commands.Writer.TryComplete();
        OnTitleChanged("Browser stopped");
        OnLoadingChanged(false, false, false);
        OnAudibleChanged(false);
        InvalidateVisual();
    }

    public override Task CloseAsync()
    {
        _ClearPopup();
        _ime.Cancel();
        _closing = true;
        OnAudibleChanged(false);
        _buffer?.Dispose();
        _buffer = null;
        _commands.Writer.TryComplete();
        return _close ??= _CloseAsync();
    }

    private async Task _CloseAsync()
    {
        try
        {
            try
            {
                await _sender;
                await _direct;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or OperationCanceledException or ObjectDisposedException or InvalidOperationException or TimeoutException)
            {
                // An input failure must not suppress the close handshake.
            }
            finally
            {
                await _host.Connection.CallAsync(_host.Generation, "enginehost", "tab.close", JsonSerializer.SerializeToElement(TabId), BrowserIpcLimits.Deadline);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or OperationCanceledException or ObjectDisposedException or InvalidOperationException or TimeoutException)
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
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is not null)
        {
            _topLevel.ScalingChanged += _ScalingChanged;
        }
        _Resize(Bounds.Size);
        _Queue("tab.visible", true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _ClearPopup();
        _ime.Cancel();
        if (_topLevel is not null)
        {
            _topLevel.ScalingChanged -= _ScalingChanged;
            _topLevel = null;
        }
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
            _ClearPopup();
            _buffer?.Dispose();
            _buffer = null;
            _Queue("tab.size", _size);
        }
    }

    private void _ScalingChanged(object? sender, EventArgs e) => _Resize(Bounds.Size);

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
            if (_popupBitmap is { } popup)
            {
                context.DrawImage(popup, new Rect(popup.PixelSize.ToSize(1)), _popupRect);
            }
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
        Interlocked.Increment(ref _focusRevision);
        _Queue("tab.input", new BrowserTabInput("focus"));
    }
    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        _ime.Cancel();
        base.OnLostFocus(e);
        Interlocked.Increment(ref _focusRevision);
        _Queue("tab.input", new BrowserTabInput("focus", Up: true));
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (IsFocused == false)
        {
            return;
        }
        OnOperatorInput();
        if (_ime.IsComposing)
        {
            e.Handled = true;
            return;
        }
        _Key(e);
        if (e.Key == Key.Enter)
        {
            _Queue("tab.input", new BrowserTabInput("key", Flags: _Modifiers(e.KeyModifiers), Key: 13, Character: 13));
        }
        e.Handled = e.KeySymbol is not { Length: > 0 } symbol || char.IsControl(symbol[0]);
    }
    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (IsFocused == false)
        {
            return;
        }
        if (_ime.IsComposing == false)
        {
            _Key(e, up: true);
        }
        e.Handled = true;
    }
    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (IsFocused == false)
        {
            return;
        }
        OnOperatorInput();
        if (_ime.Commit(e.Text ?? string.Empty))
        {
            e.Handled = true;
            return;
        }
        foreach (char character in e.Text ?? string.Empty)
        {
            _Queue("tab.input", new BrowserTabInput("key", Key: character, Character: character));
        }
        e.Handled = true;
    }
    private void _Key(KeyEventArgs e, bool up = false)
    {
        int code = KeyCodes.ToWindows(e.Key);
        if (code != 0)
        {
            _Queue("tab.input", new BrowserTabInput("key", Flags: _Modifiers(e.KeyModifiers), Key: code, Up: up));
        }
    }
    private static int _Modifiers(KeyModifiers modifiers) => (modifiers.HasFlag(KeyModifiers.Shift) ? 2 : 0)
        | (modifiers.HasFlag(KeyModifiers.Control) ? 4 : 0) | (modifiers.HasFlag(KeyModifiers.Alt) ? 8 : 0);

    private sealed class Command(string operation, JsonElement payload, long? focusRevision)
    {
        internal string Operation { get; } = operation;
        internal long? FocusRevision { get; } = focusRevision;
        internal JsonElement Payload { get; set; } = payload;
    }
}
