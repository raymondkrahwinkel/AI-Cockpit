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
    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _popupUrls = new();
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
                    if (_popupUrls.TryGetValue(openerId, out var urls) && urls.TryDequeue(out var popupUrl))
                    {
                        if (!IsWeb(popupUrl))
                        {
                            _blocked[openerId] = new("invalid_argument", "Only http(s) pages are allowed.");
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
                await SendAsync("Runtime.runIfWaitingForDebugger", null, session).ConfigureAwait(false);
                await chooser.ConfigureAwait(false);
                await SendAsync("Page.enable", null, session).ConfigureAwait(false);
                _ready.GetOrAdd(target, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
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
                if (blocked is not null && target is not null)
                {
                    _blocked[target] = blocked;
                }
                var allowed = blocked is null;
                await SendAsync(allowed ? "Fetch.continueRequest" : "Fetch.failRequest",
                    allowed ? new { requestId = data.GetProperty("requestId").GetString() } : (object)new { requestId = data.GetProperty("requestId").GetString(), errorReason = "BlockedByClient" }, session).ConfigureAwait(false);
            }
            else if (name == "Page.fileChooserOpened" && target is not null)
            {
                _blocked[target] = new("confirmation_required", "File transfer requires an operator action.");
            }
            else if (name == "Page.windowOpen" && target is not null)
            {
                _popupUrls.GetOrAdd(target, _ => new()).Enqueue(RequiredString(data, "url"));
            }
            else if (name == "Browser.downloadWillBegin")
            {
                var source = RequiredString(data, "frameId");
                _blocked[source] = new("confirmation_required", "File transfer requires an operator action.");
                if (_openers.TryGetValue(source, out var opener))
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
