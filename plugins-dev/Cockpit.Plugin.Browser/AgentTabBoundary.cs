using System.Collections.Concurrent;
using System.Text.Json;

namespace Cockpit.Plugin.Browser;

internal sealed class AgentTabBoundary(Func<string, bool> register, Action<string> closed, AddressPolicy addressPolicy) : IDisposable
{
    internal sealed record Blocked(string Code, string Error);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, string> _targets = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _ready = new();
    private readonly ConcurrentDictionary<string, Blocked> _blocked = new();
    private readonly ConcurrentDictionary<string, string> _openers = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<Popup>> _popupUrls = new();
    private sealed record Popup(string Url, ActionFeedback? Feedback);
    private readonly SemaphoreSlim _actionGate = new(1, 1);
    private readonly object _feedbackGate = new();
    private ActionFeedback? _feedback;
    private readonly ConcurrentDictionary<string, AgentNavigation> _agentNavigations = new();
    private readonly ConcurrentDictionary<(string Target, string Url), string> _pendingLocalNavigations = new();
    private readonly ConcurrentDictionary<(string Target, string Frame), byte> _localFrames = new();
    private readonly ConcurrentDictionary<string, string> _mainFrames = new();
    private readonly ConcurrentDictionary<string, string> _frameOwners = new();
    private readonly ConcurrentDictionary<string, string> _attachedOwners = new();
    private CdpPage? _browser;
    private CdpPage Browser => _browser ?? throw new IOException("The agent boundary is not connected.");
    private sealed record AgentNavigation(string Url, string? MainFrame, bool BeginsAfterBlank);

    private static string RequiredString(JsonElement data, string name) => data.GetProperty(name).GetString()
        ?? throw new IOException($"The browser response is missing {name}.");

    public static bool IsWeb(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    public async Task StartAsync(string upstream, CancellationToken token)
    {
        using var http = new HttpClient();
        using var version = JsonDocument.Parse(await http.GetStringAsync(upstream + "/json/version", token).ConfigureAwait(false));
        _browser = await CdpPage.ConnectAsync(RequiredString(version.RootElement, "webSocketDebuggerUrl"), OnEvent, token).ConfigureAwait(false);
        await DenyDownloadsAsync(token).ConfigureAwait(false);
        await _browser.CallAsync("Target.setDiscoverTargets", new { discover = true }, token).ConfigureAwait(false);
        await _browser.CallAsync("Target.setAutoAttach", new { autoAttach = true, waitForDebuggerOnStart = true, flatten = true,
            filter = new object[] { new { type = "page", exclude = false }, new { type = "iframe", exclude = false }, new { exclude = true } } }, token).ConfigureAwait(false);
    }

    public Task DenyDownloadsAsync(CancellationToken token) =>
        Browser.CallAsync("Browser.setDownloadBehavior", new { behavior = "deny", eventsEnabled = true }, token);

    public Task ReadyAsync(string target, CancellationToken token) =>
        _ready.GetOrAdd(target, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(token);

    public Blocked? TakeBlocked(string target) => _blocked.TryRemove(target, out var blocked) ? blocked : null;

    public async Task<ActionFeedback> ObserveActionAsync(string target, CancellationToken token)
    {
        await _actionGate.WaitAsync(token).ConfigureAwait(false);
        lock (_feedbackGate)
        {
            return _feedback = new ActionFeedback(this, target);
        }
    }

    // Input replies and their Page events must arrive on the same CDP connection (AC-2211).
    public Task<JsonElement> CallAsync(string target, string method, object? parameters, CancellationToken token, Action beforeSend)
    {
        var session = _targets.FirstOrDefault(pair => pair.Value == target).Key
            ?? throw new IOException("The tab has closed.");
        return Browser.CallAsync(method, parameters, token, beforeSend, session);
    }

    internal sealed class ActionFeedback(AgentTabBoundary owner, string target) : IDisposable
    {
        internal HashSet<string> Targets { get; } = [target];
        internal HashSet<string> Frames { get; } = [];
        internal bool Pending { get; set; }
        internal TaskCompletionSource<Blocked?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ExpectSignal()
        {
            lock (owner._feedbackGate)
            {
                // Native activation events can trail the input reply, including same-document navigation (AC-2211).
                foreach (var target in Targets)
                {
                    Frames.Add(owner._mainFrames.GetValueOrDefault(target, target));
                }
                Pending = true;
            }
        }

        public Task<Blocked?> WaitAsync(CancellationToken token)
        {
            lock (owner._feedbackGate)
            {
                if (!Pending)
                {
                    Completion.TrySetResult(null);
                }
            }
            return Completion.Task.WaitAsync(token);
        }

        public void Dispose()
        {
            lock (owner._feedbackGate)
            {
                owner._feedback = null;
            }
            owner._actionGate.Release();
        }
    }

    private void StartNavigation(string target, string frame)
    {
        lock (_feedbackGate)
        {
            if (_feedback is { } feedback && feedback.Targets.Contains(target))
            {
                feedback.Pending = true;
                feedback.Frames.Add(frame);
            }
        }
    }

    private void CompleteNavigation(string target, string frame)
    {
        lock (_feedbackGate)
        {
            if (_feedback is { } feedback && feedback.Targets.Contains(target) && feedback.Frames.Contains(frame))
            {
                feedback.Completion.TrySetResult(null);
            }
        }
    }

    private void SetBlocked(string target, Blocked blocked)
    {
        _blocked[target] = blocked;
        lock (_feedbackGate)
        {
            if (_feedback is { } feedback && feedback.Targets.Contains(target))
            {
                feedback.Completion.TrySetResult(blocked);
            }
        }
    }

    public async Task PermitAgentNavigationAsync(string target, Uri url, CancellationToken token, bool beginsAfterBlank = false)
    {
        foreach (var key in _pendingLocalNavigations.Keys.Where(key => key.Target == target)) _pendingLocalNavigations.TryRemove(key, out _);
        var session = _targets.FirstOrDefault(pair => pair.Value == target).Key;
        string? mainFrame = null;
        if (session is not null && !beginsAfterBlank)
        {
            try
            {
                var tree = await SendAsync("Page.getFrameTree", null, session).ConfigureAwait(false);
                mainFrame = RequiredString(tree.GetProperty("frameTree").GetProperty("frame"), "id");
                _mainFrames[target] = mainFrame;
            }
            catch (Exception ex) when (AgentOwnedTabs.IsBrowserFailure(ex))
            {
                // A just-attached popup is paused before it has a queryable frame tree.
            }
        }
        _agentNavigations[target] = new(url.GetLeftPart(UriPartial.Query), mainFrame, beginsAfterBlank);
    }

    private void OnEvent(string name, JsonElement data, string? session)
    {
        _ = HandleAsync(name, data.Clone(), session);
    }

    private async Task HandleAsync(string name, JsonElement data, string? session)
    {
        string? target = session is not null && _targets.TryGetValue(session, out var id) ? id : null;
        try
        {
            if (name == "Target.targetDestroyed")
            {
                var destroyed = RequiredString(data, "targetId");
                if (_attachedOwners.TryRemove(destroyed, out var owner) && owner != destroyed) return;
                ClearTarget(destroyed);
                _ = Task.Run(() => closed(destroyed));
                return;
            }
            if (name == "Target.attachedToTarget")
            {
                session = RequiredString(data, "sessionId");
                var info = data.GetProperty("targetInfo");
                var attachedTarget = RequiredString(info, "targetId");
                target = attachedTarget;
                if (info.TryGetProperty("parentFrameId", out var parentFrame) && parentFrame.GetString() is { } parentId
                    && _frameOwners.TryGetValue(parentId, out var owner)) target = owner;
                _targets[session] = target;
                _attachedOwners[attachedTarget] = target;
                if (info.TryGetProperty("openerId", out var opener))
                {
                    var openerId = opener.GetString() ?? throw new IOException("The popup has no opener.");
                    _openers[target] = openerId;
                    if (_popupUrls.TryGetValue(openerId, out var urls) && urls.TryDequeue(out var popup))
                    {
                        lock (_feedbackGate)
                        {
                            if (_feedback is { } feedback && ReferenceEquals(feedback, popup.Feedback))
                            {
                                feedback.Targets.Add(target);
                                feedback.Frames.Add(attachedTarget);
                            }
                        }
                        var popupUrl = popup.Url;
                        if (!IsWeb(popupUrl))
                        {
                            _blocked[openerId] = new("invalid_argument", "Only http(s) pages are allowed.");
                            SetBlocked(target, _blocked[openerId]);
                            var close = CloseAsync(attachedTarget);
                            // Chromium needs the paused renderer released to finish the already requested close.
                            await SendAsync("Runtime.runIfWaitingForDebugger", null, session).ConfigureAwait(false);
                            await close.ConfigureAwait(false);
                            return;
                        }
                        if (_localFrames.Keys.Any(key => key.Target == openerId))
                        {
                            await PermitAgentNavigationAsync(target, new Uri(popupUrl, UriKind.Absolute), _lifetime.Token, beginsAfterBlank: true).ConfigureAwait(false);
                        }
                    }
                }
                await SendAsync("Fetch.enable", new { patterns = new object[] {
                    new { urlPattern = "*", requestStage = "Request" },
                    new { urlPattern = "*", resourceType = "Document", requestStage = "Response" } } }, session).ConfigureAwait(false);
                await SendAsync("Target.setAutoAttach", new { autoAttach = true, waitForDebuggerOnStart = true, flatten = true }, session).ConfigureAwait(false);
                var url = info.GetProperty("url").GetString();
                if (url is { Length: > 0 } && url != "about:blank" && !IsWeb(url))
                {
                    await CloseAsync(attachedTarget).ConfigureAwait(false);
                    return;
                }
                if (data.GetProperty("waitingForDebugger").GetBoolean() && !register(target))
                {
                    var close = CloseAsync(attachedTarget);
                    await SendAsync("Runtime.runIfWaitingForDebugger", null, session).ConfigureAwait(false);
                    await close.ConfigureAwait(false);
                    return;
                }
                var chooser = SendAsync("Page.setInterceptFileChooserDialog", new { enabled = true }, session);
                var pageEvents = SendAsync("Page.enable", null, session);
                await SendAsync("Runtime.runIfWaitingForDebugger", null, session).ConfigureAwait(false);
                await chooser.ConfigureAwait(false);
                await pageEvents.ConfigureAwait(false);
                _ready.GetOrAdd(target, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
            }
            else if (name is "Page.frameRequestedNavigation" or "Page.frameStartedNavigating" && target is not null)
            {
                StartNavigation(target, RequiredString(data, "frameId"));
            }
            else if (name == "Page.navigatedWithinDocument" && target is not null)
            {
                CompleteNavigation(target, RequiredString(data, "frameId"));
            }
            else if (name == "Page.frameNavigated" && target is not null)
            {
                var frame = data.GetProperty("frame");
                var frameId = RequiredString(frame, "id");
                var url = RequiredString(frame, "url");
                var key = (target, Uri.TryCreate(url, UriKind.Absolute, out var navigationUri) ? navigationUri.GetLeftPart(UriPartial.Query) : url);
                var parentId = frame.TryGetProperty("parentId", out var parent) ? parent.GetString() : null;
                _frameOwners[frameId] = target;
                if (parentId is null) _mainFrames[target] = frameId;
                var inherited = url is "about:blank" or "about:srcdoc" && parentId is not null && _localFrames.ContainsKey((target, parentId));
                var pending = _pendingLocalNavigations.TryGetValue(key, out var pendingFrame) && pendingFrame == frameId
                    && _pendingLocalNavigations.TryRemove(key, out _);
                var local = pending || inherited || await IsLocalAddressAsync(url).ConfigureAwait(false);
                if (local)
                {
                    _localFrames[(target, frameId)] = 0;
                }
                else
                {
                    _localFrames.TryRemove((target, frameId), out _);
                }
                if (url != "about:blank")
                {
                    CompleteNavigation(target, frameId);
                }
            }
            else if (name == "Fetch.requestPaused" && session is not null)
            {
                var requestUrl = data.GetProperty("request").GetProperty("url").GetString();
                var blocked = data.TryGetProperty("responseStatusCode", out _) ? null : await CheckAddressAsync(requestUrl, target, data).ConfigureAwait(false);
                if (data.TryGetProperty("responseHeaders", out var headers))
                {
                    foreach (var header in headers.EnumerateArray())
                    {
                        var location = string.Equals(header.GetProperty("name").GetString(), "Location", StringComparison.OrdinalIgnoreCase);
                        if (location && Uri.TryCreate(requestUrl, UriKind.Absolute, out var requestUri) && Uri.TryCreate(requestUri, header.GetProperty("value").GetString(), out var redirect))
                        {
                            var document = data.TryGetProperty("resourceType", out var resourceType) && resourceType.GetString() == "Document";
                            var frame = data.TryGetProperty("frameId", out var frameId) ? frameId.GetString() : null;
                            var directLocalRedirect = target is not null && document
                                && frame is not null && _mainFrames.TryGetValue(target, out var mainFrame) && frame == mainFrame
                                && _pendingLocalNavigations.TryGetValue((target, requestUri.AbsoluteUri), out var pendingFrame) && pendingFrame == frame
                                && _pendingLocalNavigations.TryRemove((target, requestUri.AbsoluteUri), out _);
                            blocked ??= await CheckAddressAsync(redirect.AbsoluteUri, target, data, directLocalRedirect).ConfigureAwait(false);
                        }
                    }
                }
                var allowed = blocked is null;
                // Record the reason before failing the request: Chromium may report the net error before it would be stored.
                if (blocked is not null && target is not null)
                {
                    SetBlocked(target, blocked);
                }
                await SendAsync(allowed ? "Fetch.continueRequest" : "Fetch.failRequest",
                    allowed ? new { requestId = data.GetProperty("requestId").GetString() } : (object)new { requestId = data.GetProperty("requestId").GetString(), errorReason = "BlockedByClient" }, session).ConfigureAwait(false);
            }
            else if (name == "Page.fileChooserOpened" && target is not null)
            {
                SetBlocked(target, new("confirmation_required", "File transfer requires an operator action."));
            }
            else if (name == "Page.windowOpen" && target is not null)
            {
                lock (_feedbackGate)
                {
                    var feedback = _feedback is { } current && current.Targets.Contains(target) ? current : null;
                    if (feedback is not null)
                    {
                        feedback.Pending = true;
                    }
                    _popupUrls.GetOrAdd(target, _ => new()).Enqueue(new(RequiredString(data, "url"), feedback));
                }
            }
            else if (name == "Browser.downloadWillBegin")
            {
                var source = RequiredString(data, "frameId");
                var owner = _frameOwners.GetValueOrDefault(source, source);
                SetBlocked(owner, new("confirmation_required", "File transfer requires an operator action."));
                if (_openers.TryGetValue(owner, out var opener))
                {
                    _blocked[opener] = new("confirmation_required", "File transfer requires an operator action.");
                }
            }
        }
        catch (Exception ex) when (AgentOwnedTabs.IsBrowserFailure(ex))
        {
            if (target is not null)
            {
                _ready.GetOrAdd(target, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetException(ex);
                try { await CloseAsync(target).ConfigureAwait(false); }
                catch (Exception close) when (AgentOwnedTabs.IsBrowserFailure(close)) { }
            }
        }
    }

    private async Task<Blocked?> CheckAddressAsync(string? url, string? target, JsonElement request, bool inheritedAgentNavigation = false)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return new("invalid_argument", "Only http(s) pages are allowed.");
        }
        var document = request.TryGetProperty("resourceType", out var resourceType) && resourceType.GetString() == "Document";
        var frame = request.TryGetProperty("frameId", out var frameId) ? frameId.GetString() : null;
        var requestUrl = uri.GetLeftPart(UriPartial.Query);
        var mainFrame = target is not null && _mainFrames.TryGetValue(target, out var knownMainFrame) ? knownMainFrame : null;
        var mainDocument = document && frame is not null && frame == mainFrame;
        var agentNavigation = inheritedAgentNavigation || target is not null && document
            && mainDocument && _pendingLocalNavigations.TryGetValue((target, uri.AbsoluteUri), out var pendingMainFrame)
            && pendingMainFrame == frame && _pendingLocalNavigations.TryRemove((target, uri.AbsoluteUri), out _);
        if (target is not null && document && _agentNavigations.TryGetValue(target, out var navigation)
            && (navigation.BeginsAfterBlank || navigation.MainFrame is null ? requestUrl == navigation.Url : frame == navigation.MainFrame)
            && _agentNavigations.TryRemove(target, out _))
        {
            agentNavigation = requestUrl == navigation.Url;
            if (agentNavigation && frame is not null)
            {
                _mainFrames[target] = frame;
                mainDocument = true;
            }
        }
        var resolved = await addressPolicy.ResolveAsync(uri, _lifetime.Token).ConfigureAwait(false);
        if (resolved.Reason is not null)
        {
            return new("network_blocked", resolved.Reason);
        }
        var localAddress = addressPolicy.IsOwnMachineOnly(resolved.Addresses);
        if (!localAddress)
        {
            if (target is not null && document && frame is not null) _localFrames.TryRemove((target, frame), out _);
            if (target is not null && mainDocument)
                foreach (var key in _pendingLocalNavigations.Keys.Where(key => key.Target == target)) _pendingLocalNavigations.TryRemove(key, out _);
            return null;
        }
        if (target is null) return null;

        var localPage = frame is not null && _localFrames.ContainsKey((target, frame));
        if (!agentNavigation && !localPage)
        {
            return new("network_blocked", "Network access blocked: a local service may only be opened by the agent or by a local page.");
        }
        if (mainDocument)
        {
            _pendingLocalNavigations[(target, uri.AbsoluteUri)] = frame!;
        }
        return null;
    }

    private void ClearTarget(string target)
    {
        _agentNavigations.TryRemove(target, out _);
        foreach (var key in _pendingLocalNavigations.Keys.Where(key => key.Target == target)) _pendingLocalNavigations.TryRemove(key, out _);
        foreach (var key in _localFrames.Keys.Where(key => key.Target == target)) _localFrames.TryRemove(key, out _);
        _mainFrames.TryRemove(target, out _);
        foreach (var frame in _frameOwners.Where(entry => entry.Value == target).Select(entry => entry.Key)) _frameOwners.TryRemove(frame, out _);
    }


    private async Task<bool> IsLocalAddressAsync(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" && addressPolicy.IsOwnMachineOnly((await addressPolicy.ResolveAsync(uri, _lifetime.Token).ConfigureAwait(false)).Addresses);


    private Task<JsonElement> SendAsync(string method, object? parameters, string session) =>
        Browser.CallAsync(method, parameters, _lifetime.Token, sessionId: session);

    private Task CloseAsync(string target) => Browser.CallAsync("Target.closeTarget", new { targetId = target }, _lifetime.Token);

    public void Dispose()
    {
        _lifetime.Cancel();
        _browser?.Dispose();
        _lifetime.Dispose();
    }
}
