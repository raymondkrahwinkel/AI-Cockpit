using System.Collections.Concurrent;
using System.Text.Json;

namespace Cockpit.Plugin.Browser;

internal sealed class AgentTabBoundary(Func<string, bool> register, Action<string> closed) : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, string> _targets = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _ready = new();
    private readonly ConcurrentDictionary<string, string> _blocked = new();
    private readonly ConcurrentDictionary<string, string> _openers = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _popupUrls = new();
    private CdpPage? _browser;

    public static bool IsWeb(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    public async Task StartAsync(string upstream, CancellationToken token)
    {
        using var http = new HttpClient();
        using var version = JsonDocument.Parse(await http.GetStringAsync(upstream + "/json/version", token).ConfigureAwait(false));
        _browser = await CdpPage.ConnectAsync(version.RootElement.GetProperty("webSocketDebuggerUrl").GetString()!, OnEvent, token).ConfigureAwait(false);
        await DenyDownloadsAsync(token).ConfigureAwait(false);
        await _browser.CallAsync("Target.setDiscoverTargets", new { discover = true }, token).ConfigureAwait(false);
        await _browser.CallAsync("Target.setAutoAttach", new { autoAttach = true, waitForDebuggerOnStart = true, flatten = true,
            filter = new object[] { new { type = "page", exclude = false }, new { exclude = true } } }, token).ConfigureAwait(false);
    }

    public Task DenyDownloadsAsync(CancellationToken token) =>
        _browser!.CallAsync("Browser.setDownloadBehavior", new { behavior = "deny", eventsEnabled = true }, token);

    public Task ReadyAsync(string target, CancellationToken token) =>
        _ready.GetOrAdd(target, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(token);

    public string? TakeBlocked(string target) => _blocked.TryRemove(target, out var code) ? code : null;

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
                var destroyed = data.GetProperty("targetId").GetString()!;
                _ = Task.Run(() => closed(destroyed));
                return;
            }
            if (name == "Target.attachedToTarget")
            {
                session = data.GetProperty("sessionId").GetString()!;
                var info = data.GetProperty("targetInfo");
                target = info.GetProperty("targetId").GetString()!;
                _targets[session] = target;
                if (info.TryGetProperty("openerId", out var opener))
                {
                    var openerId = opener.GetString()!;
                    _openers[target] = openerId;
                    if (_popupUrls.TryGetValue(openerId, out var urls) && urls.TryDequeue(out var popupUrl) && !IsWeb(popupUrl))
                    {
                        _blocked[openerId] = "invalid_argument";
                        var close = CloseAsync(target);
                        // Chromium needs the paused renderer released to finish the already requested close.
                        await SendAsync("Runtime.runIfWaitingForDebugger", null, session).ConfigureAwait(false);
                        await close.ConfigureAwait(false);
                        return;
                    }
                }
                await SendAsync("Fetch.enable", new { patterns = new[] {
                    new { urlPattern = "*", resourceType = "Document", requestStage = "Request" },
                    new { urlPattern = "*", resourceType = "Document", requestStage = "Response" } } }, session).ConfigureAwait(false);
                await SendAsync("Target.setAutoAttach", new { autoAttach = true, waitForDebuggerOnStart = true, flatten = true }, session).ConfigureAwait(false);
                var url = info.GetProperty("url").GetString();
                if (url is { Length: > 0 } && url != "about:blank" && !IsWeb(url))
                {
                    await CloseAsync(target).ConfigureAwait(false);
                    return;
                }
                if (data.GetProperty("waitingForDebugger").GetBoolean() && !register(target))
                {
                    var close = CloseAsync(target);
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
            else if (name == "Fetch.requestPaused" && session is not null)
            {
                var allowed = IsWeb(data.GetProperty("request").GetProperty("url").GetString());
                if (data.TryGetProperty("responseHeaders", out var headers))
                {
                    foreach (var header in headers.EnumerateArray())
                    {
                        if (header.GetProperty("name").GetString()!.Equals("Location", StringComparison.OrdinalIgnoreCase)
                            && Uri.TryCreate(new Uri(data.GetProperty("request").GetProperty("url").GetString()!), header.GetProperty("value").GetString(), out var redirect))
                            allowed &= IsWeb(redirect.AbsoluteUri);
                    }
                }
                if (!allowed && target is not null) _blocked[target] = "invalid_argument";
                await SendAsync(allowed ? "Fetch.continueRequest" : "Fetch.failRequest",
                    allowed ? new { requestId = data.GetProperty("requestId").GetString() } : (object)new { requestId = data.GetProperty("requestId").GetString(), errorReason = "BlockedByClient" }, session).ConfigureAwait(false);
            }
            else if (name == "Page.fileChooserOpened" && target is not null)
            {
                _blocked[target] = "confirmation_required";
            }
            else if (name == "Page.windowOpen" && target is not null)
            {
                _popupUrls.GetOrAdd(target, _ => new()).Enqueue(data.GetProperty("url").GetString()!);
            }
            else if (name == "Browser.downloadWillBegin")
            {
                var source = data.GetProperty("frameId").GetString()!;
                _blocked[source] = "confirmation_required";
                if (_openers.TryGetValue(source, out var opener)) _blocked[opener] = "confirmation_required";
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

    private Task<JsonElement> SendAsync(string method, object? parameters, string session) =>
        _browser!.CallAsync(method, parameters, _lifetime.Token, sessionId: session);

    private Task CloseAsync(string target) => _browser!.CallAsync("Target.closeTarget", new { targetId = target }, _lifetime.Token);

    public void Dispose()
    {
        _lifetime.Cancel();
        _browser?.Dispose();
        _lifetime.Dispose();
    }
}
