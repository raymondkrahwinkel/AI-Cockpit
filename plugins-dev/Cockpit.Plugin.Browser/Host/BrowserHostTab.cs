using System.Collections.Concurrent;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Cockpit.Plugin.Browser.Ipc;
using Xilium.CefGlue;

namespace Cockpit.Plugin.Browser.Host;

internal sealed partial class BrowserHostTab : IAsyncDisposable
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
    private byte[] _pendingPixels = [];
    private long _pendingFrameId;
    private bool _closing;
    private bool _focused;
    private bool _creationPending;
    private long _frameId;
    private long _bufferRevision;
    private Task _frameSender = Task.CompletedTask;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<string?>> _devTools = new();
    private int _devToolsId;
    private CefRegistration? _devToolsObserver;

    // AC-2149: the control this host enforces, set only by tab.control: the controller's origin (submissions wait while
    // set), the grant that may act now, and the revision that set them. A new host process starts with none of it.
    private readonly object _control = new();
    private volatile string? _controlOrigin;
    private Guid? _actingGrant;
    private long _controlRevision;
    private CefCallback? _held;
    private BrowserTabHeld? _heldSubmit;
    private long _heldId;

    // AC-2148: exactly the methods TabShare and its control path use, so a tab grant never becomes general DevTools access.
    private static readonly HashSet<string> DevToolsMethods = ["Page.captureScreenshot", "Accessibility.getFullAXTree",
        "Runtime.evaluate", "DOM.enable", "DOM.getNodeForLocation", "Page.getFrameTree", "DOM.describeNode"];

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
        _creationPending = true;
        try
        {
            CefBrowserHost.CreateBrowser(info, new Client(this), new CefBrowserSettings { WindowlessFrameRate = 60 }, _state.Address, null, profile);
        }
        catch
        {
            _creationPending = false;
            throw;
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
                _pendingPixels = [];
                _pendingFrameId = 0;
                host.NotifyScreenInfoChanged();
                host.WasResized();
                break;
            case "tab.visible":
                if (message.Payload.GetBoolean() == false)
                {
                    _focused = false;
                    host.SetFocus(false);
                }
                host.WasHidden(message.Payload.GetBoolean() == false);
                break;
            case "tab.input":
                var input = message.Payload.Deserialize<BrowserTabInput>() ?? throw new InvalidDataException("Missing browser input.");
                // AC-2153: CEF still accepts editing shortcuts after SetFocus(false), including OS clipboard access.
                if (input.Kind == "key" && _focused == false)
                {
                    break;
                }
                if (input.Up == false && input.Kind is ("click" or "wheel" or "key"))
                {
                    _TakeOver();
                }
                _Input(host, input);
                break;
            default: throw new InvalidDataException("Unknown browser tab operation.");
        }
    }, token);

    // AC-2148: one DevTools method on this tab's own browser, without a debugging port. The IPC envelope already bound
    // the call to this tab, session and generation; an unanswered method ends with the call's deadline or the browser.
    internal async Task<JsonElement> DevToolsAsync(BrowserIpcMessage message, CancellationToken token)
    {
        var call = message.Payload.Deserialize<BrowserTabDevTools>() ?? throw new InvalidDataException("Missing DevTools call.");
        if (DevToolsMethods.Contains(call.Method) == false)
        {
            throw new InvalidDataException("DevTools method refused.");
        }
        int id = Interlocked.Increment(ref _devToolsId);
        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _devTools[id] = pending;
        try
        {
            using var cancel = token.Register(() => pending.TrySetCanceled(token));
            await OnCefAsync(() =>
            {
                if (_browser is not { } browser || _closing)
                {
                    throw new InvalidDataException("The browser tab is closed.");
                }
                var arguments = CefDictionaryValue.Create();
                foreach (var (key, value) in call.Parameters)
                {
                    _ = value is JsonElement element ? element.ValueKind switch
                    {
                        JsonValueKind.True or JsonValueKind.False => arguments.SetBool(key, element.GetBoolean()),
                        JsonValueKind.Number when element.TryGetInt32(out int number) => arguments.SetInt(key, number),
                        JsonValueKind.String => arguments.SetString(key, element.GetString()),
                        _ => throw new InvalidDataException("Invalid DevTools parameter."),
                    } : throw new InvalidDataException("Invalid DevTools parameter.");
                }
                var host = browser.GetHost();
                _devToolsObserver ??= host.AddDevToolsMessageObserver(new DevToolsObserver(this));
                if (host.ExecuteDevToolsMethod(id, call.Method, arguments) == 0)
                {
                    pending.TrySetResult(null);
                }
            }, token);
            return JsonSerializer.SerializeToElement(await pending.Task ?? throw new InvalidDataException("The DevTools method failed."));
        }
        finally
        {
            _devTools.TryRemove(id, out _);
        }
    }

    private void _FailDevTools()
    {
        foreach (var pending in _devTools.Values)
        {
            pending.TrySetResult(null);
        }
    }

    // AC-2149: what the parent's consent decided, applied at once rather than through CEF's queue, so it also reaches
    // an agent action already posted there. Any change drops the held submission; revisions only ever go up.
    internal JsonElement Control(BrowserIpcMessage message)
    {
        var control = message.Payload.Deserialize<BrowserTabControl>() ?? throw new InvalidDataException("Missing browser control.");
        if ((control.Origin is { } address && ControlPolicy.SameOrigin(address, ControlPolicy.OriginOf(address)) == false) ||
            (control.Grant is not null && control.Origin is null))
        {
            throw new InvalidDataException("Invalid browser control.");
        }
        lock (_control)
        {
            if (control.Revision <= _controlRevision)
            {
                throw new InvalidDataException("Stale browser control.");
            }
            _controlRevision = control.Revision;
            _CancelFiles();
            _controlOrigin = control.Origin is null ? null : ControlPolicy.OriginOf(control.Origin);
            _actingGrant = control.Grant;
            _DropHeld();
        }
        return JsonSerializer.SerializeToElement(true);
    }

    // AC-2149: one agent action, decided on CEF's UI thread right before it goes in: exactly the acting grant, the main
    // frame on its origin, for keys the focused frame too, for a click the frames of the hit test, for navigation the
    // target. The lock keeps a control change from landing between that decision and the input.
    internal async Task<JsonElement> AgentAsync(BrowserIpcMessage message, CancellationToken token)
    {
        var agent = message.Payload.Deserialize<BrowserTabAgent>() ?? throw new InvalidDataException("Missing agent action.");
        Action<CefBrowserHost> input = agent switch
        {
            { Kind: "click", Frames: not null } => host => AgentInput.Click(host, agent.X, agent.Y),
            { Kind: "type", Text: { Length: > 0 and <= ControlPolicy.MaxTyped } text } => host => AgentInput.Type(host, text),
            { Kind: "key", Key: > 0 and <= 0xFE } => host => AgentInput.Key(host, agent.Key),
            { Kind: "scroll" } => host => AgentInput.Scroll(host, _size.Width / 2, _size.Height / 2, agent.X, agent.Y),
            { Kind: "navigate", Text: { } url } => host => host.GetBrowser().GetMainFrame().LoadUrl(url),
            _ => throw new InvalidDataException("Invalid agent action."),
        };
        bool done = false;
        await OnCefAsync(() =>
        {
            if (_browser is not { } browser || _closing)
            {
                return;
            }
            lock (_control)
            {
                var origin = _controlOrigin;
                done = ControlPolicy.MayAct(_actingGrant == agent.Grant, origin, browser.GetMainFrame()?.Url, browser.GetFocusedFrame()?.Url, agent.NeedsFocus)
                    && (agent.Kind != "click" || ControlPolicy.FrameSignature(browser) == agent.Frames)
                    && (agent.Kind != "navigate" || (origin is not null && ControlPolicy.SameOrigin(agent.Text, origin)));
                if (done)
                {
                    input(browser.GetHost());
                }
            }
        }, token);
        return JsonSerializer.SerializeToElement(done);
    }

    internal async Task<JsonElement> FramesAsync(CancellationToken token)
    {
        string? frames = null;
        await OnCefAsync(() => frames = _browser is { } browser && _closing == false ? ControlPolicy.FrameSignature(browser) : null, token);
        return JsonSerializer.SerializeToElement(frames);
    }

    // AC-2149: Send continues exactly the held request, body and all, only while it is still the one held and the tab
    // still has a controller on its target's origin; anything else cancels it.
    internal async Task<JsonElement> ReleaseAsync(BrowserIpcMessage message, CancellationToken token)
    {
        var release = message.Payload.Deserialize<BrowserTabRelease>() ?? throw new InvalidDataException("Missing held submission.");
        await OnCefAsync(() =>
        {
            lock (_control)
            {
                if (release.Id != _heldId || _held is not { } held || _heldSubmit is not { } submit)
                {
                    return;
                }
                if (release.Send && _controlOrigin is { } origin && ControlPolicy.SameOrigin(submit.Url, origin))
                {
                    _held = null;
                    _heldSubmit = null;
                    _PostState(heldSubmit: null);
                    held.Continue();
                    return;
                }
                _DropHeld();
            }
        }, token);
        return JsonSerializer.SerializeToElement(true);
    }

    // The operator's own click, wheel or key: the agent stops acting here until the parent gives control back.
    private void _TakeOver()
    {
        lock (_control)
        {
            if (_actingGrant is not null)
            {
                _actingGrant = null;
                _DropHeld();
            }
        }
    }

    // Callers hold _control. Cancelling a CefCallback is safe on any thread.
    private void _DropHeld()
    {
        if (_held is { } held)
        {
            _held = null;
            _heldSubmit = null;
            _PostState(heldSubmit: null);
            held.Cancel();
        }
    }

    // Callers hold _control, so the state posts reach CEF's queue in the order the hold changed.
    private void _PostState(BrowserTabHeld? heldSubmit) => CefRuntime.PostTask(CefThreadId.UI, new ActionTask(() => _State(_state with { Held = heldSubmit })));

    private static void _ValidateSize(BrowserTabSize size)
    {
        if (size.Width < 1 || size.Height < 1 || double.IsFinite(size.Scale) == false || size.Scale < 0.5 || size.Scale > 8 || size.Revision < 1)
        {
            throw new InvalidDataException("Invalid browser size.");
        }
        _ = new BrowserIpcFrameDescriptor(Guid.NewGuid(), Guid.NewGuid(), checked((int)Math.Ceiling(size.Width * size.Scale)),
            checked((int)Math.Ceiling(size.Height * size.Scale))).ByteLength;
    }

    private void _Input(CefBrowserHost host, BrowserTabInput input)
    {
        var mouse = new CefMouseEvent(input.X, input.Y, (CefEventFlags)input.Flags);
        switch (input.Kind)
        {
            case "move": host.SendMouseMoveEvent(mouse, input.Up); break;
            case "click" when Enum.IsDefined((CefMouseButtonType)input.Button) && input.Count is > 0 and <= 3:
                _focused = true;
                host.SetFocus(true);
                host.SendMouseClickEvent(mouse, (CefMouseButtonType)input.Button, input.Up, input.Count);
                break;
            case "wheel": host.SendMouseWheelEvent(mouse, input.DeltaX, input.DeltaY); break;
            case "focus":
                _focused = input.Up == false;
                host.SetFocus(_focused);
                break;
            case "key" when input.Character is >= 0 and <= char.MaxValue:
                host.SendKeyEvent(new CefKeyEvent
                {
                    EventType = input.Character != 0 ? CefKeyEventType.Char : input.Up ? CefKeyEventType.KeyUp : CefKeyEventType.RawKeyDown,
                    WindowsKeyCode = input.Key, Character = (char)input.Character, UnmodifiedCharacter = (char)input.Character,
                    Modifiers = (CefEventFlags)input.Flags,
                    IsSystemKey = (input.Flags & (int)CefEventFlags.AltDown) != 0,
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
        if (_bufferRevision != _size.Revision)
        {
            _dirty = new CefRectangle(0, 0, width, height);
        }
        if (_inflight)
        {
            // AC-2147: CEF releases these pixels after OnPaint, before the receiver may have acknowledged.
            int length = checked(width * height * 4);
            if (_pendingPixels.Length != length)
            {
                _pendingPixels = new byte[length];
            }
            for (int row = _dirty.Y; row < _dirty.Y + _dirty.Height; row++)
            {
                int offset = (row * width + _dirty.X) * 4;
                Marshal.Copy(pixels + offset, _pendingPixels, offset, _dirty.Width * 4);
            }
            _pendingFrameId = frameId;
            return;
        }
        _Publish(pixels, width, height, frameId);
    }

    private void _Publish(nint pixels, int width, int height, long frameId)
    {
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
                if (_closing == false && _pendingFrameId != 0)
                {
                    long pendingFrameId = _pendingFrameId;
                    _pendingFrameId = 0;
                    unsafe
                    {
                        fixed (byte* pixels = _pendingPixels)
                        {
                            _Publish((nint)pixels, (int)Math.Ceiling(_size.Width * _size.Scale), (int)Math.Ceiling(_size.Height * _size.Scale), pendingFrameId);
                        }
                    }
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
        Task[] files = [];
        await OnCefAsync(() =>
        {
            _closing = true;
            lock (_control)
            {
                _CancelFiles();
                files = _fileDialogs.Values.Select(dialog => dialog.Task).ToArray();
            }
            if (_browser is { } browser)
            {
                browser.GetHost().CloseBrowser(true);
            }
            else if (_creationPending == false)
            {
                _closed.TrySetResult();
            }
        }, CancellationToken.None);
        await Task.WhenAll(files);
        _states.Writer.TryComplete();
        await _closed.Task.WaitAsync(BrowserIpcLimits.Deadline);
        await Task.WhenAll(_frameSender, _stateSender);
        _buffer?.Dispose();
        _pendingPixels = [];
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
        private readonly DialogHandler _dialog = new(owner);
        private readonly DownloadHandler _download = new(owner);
        protected override CefDownloadHandler GetDownloadHandler() => _download;
        protected override CefDialogHandler GetDialogHandler() => _dialog;
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
            owner._devToolsObserver?.Dispose();
            owner._devToolsObserver = null;
            owner._FailDevTools();
            lock (owner._control)
            {
                owner._controlOrigin = null;
                owner._actingGrant = null;
                owner._DropHeld();
                owner._CancelFiles();
            }
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

    // AC-2149: a controlled tab's navigations go through the submit hold, here in the host; others keep CEF's defaults.
    private sealed class RequestHandler(BrowserHostTab owner) : CefRequestHandler
    {
        private readonly SubmitHold _hold = new(owner);
        protected override CefResourceRequestHandler? GetResourceRequestHandler(CefBrowser browser, CefFrame frame, CefRequest request,
            bool isNavigation, bool isDownload, string requestInitiator, ref bool disableDefaultHandling) =>
            isNavigation && owner._controlOrigin is not null ? _hold : null;
        protected override void OnRenderProcessTerminated(CefBrowser browser, CefTerminationStatus status, int errorCode, string errorString)
        {
            owner._FailDevTools();
            lock (owner._control)
            {
                owner._DropHeld();
            }
            owner._State(owner._state with { Failure = "The page renderer stopped. Reload the tab to try again." });
        }
    }

    // The real request waits (ContinueAsync) for the operator in every frame; another origin is cancelled outright.
    // Decided under the control lock, so a revocation between the verdict and the hold cannot leave it waiting.
    private sealed class SubmitHold(BrowserHostTab owner) : CefResourceRequestHandler
    {
        protected override CefCookieAccessFilter? GetCookieAccessFilter(CefBrowser browser, CefFrame frame, CefRequest request) => null;
        protected override CefReturnValue OnBeforeResourceLoad(CefBrowser browser, CefFrame frame, CefRequest request, CefCallback callback)
        {
            var source = request.TransitionType & CefTransitionType.SourceMask;
            var (method, url) = (request.Method, request.Url);
            lock (owner._control)
            {
                switch (ControlPolicy.Navigation(owner._controlOrigin, method, source == CefTransitionType.FormSubmit, url, frame.IsMain, source == CefTransitionType.Explicit))
                {
                    case ControlPolicy.Verdict.Drop:
                        return CefReturnValue.Cancel;
                    case ControlPolicy.Verdict.Hold:
                        owner._DropHeld();
                        owner._held = callback;
                        owner._heldSubmit = new BrowserTabHeld(++owner._heldId, method, url);
                        owner._PostState(owner._heldSubmit);
                        return CefReturnValue.ContinueAsync;
                    default:
                        return CefReturnValue.Continue;
                }
            }
        }
    }

    // No file picker opens while an agent controls the tab, so a click cannot hand it a file.
    private sealed class DialogHandler(BrowserHostTab owner) : CefDialogHandler
    {
        protected override bool OnFileDialog(CefBrowser browser, CefFileDialogMode mode, string title, string defaultFilePath,
            string[] acceptFilters, string[] acceptExtensions, string[] acceptDescriptions, CefFileDialogCallback callback)
        {
            lock (owner._control)
            {
                if (owner._controlOrigin is not null || owner._closing)
                {
                    callback.Cancel();
                    return true;
                }
                _ = owner._ChooseFilesAsync(new BrowserTabFileDialog((int)mode, title, defaultFilePath, acceptFilters),
                    paths =>
                    {
                        if (paths.Length == 0)
                        {
                            callback.Cancel();
                        }
                        else
                        {
                            callback.Continue(paths);
                        }
                        callback.Dispose();
                    });
            }
            return true;
        }
    }

    // Only method results are wanted; raw messages and events pass through untouched.
    private sealed class DevToolsObserver(BrowserHostTab owner) : CefDevToolsMessageObserver
    {
        protected override bool OnDevToolsMessage(CefBrowser browser, nint message, int messageSize) => false;
        protected override void OnDevToolsMethodResult(CefBrowser browser, int messageId, bool success, nint result, int resultSize)
        {
            if (owner._devTools.TryGetValue(messageId, out var pending))
            {
                pending.TrySetResult(success ? Marshal.PtrToStringUTF8(result, resultSize) : null);
            }
        }
        protected override void OnDevToolsEvent(CefBrowser browser, string method, nint parameters, int parametersSize) { }
        protected override void OnDevToolsAgentAttached(CefBrowser browser) { }
        protected override void OnDevToolsAgentDetached(CefBrowser browser) { }
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
