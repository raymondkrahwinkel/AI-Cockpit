using System.Text.Json;

namespace Cockpit.Plugin.Browser;

internal sealed partial class AgentOwnedTabs
{
    public async Task<TabShareAnswer> ActAsync(string caller, TabShareRequest action, string? loadState = null, int timeoutMs = 30_000)
    {
        try
        {
            Validate(action, loadState, timeoutMs);
            if (action.TabId is null || !_tabs.TryGetValue(action.TabId, out var entry) || entry.Owner != caller)
            {
                throw new OwnedTabException("not_owner", "This tab does not belong to this session.");
            }
            var mutate = action.Tool != "wait";
            var session = Check(caller, entry.Generation, mutate);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
            using var ended = session.Ended.Register(timeout.Cancel);
            var token = timeout.Token;
            var boundary = await BoundaryAsync(caller, session, token).ConfigureAwait(false);
            boundary.TakeBlocked(entry.Target);
            await boundary.DenyDownloadsAsync(token).ConfigureAwait(false);
            using var targets = JsonDocument.Parse(await Http.GetStringAsync(session.Upstream + "/json/list", token).ConfigureAwait(false));
            var target = targets.RootElement.EnumerateArray().FirstOrDefault(p => p.GetProperty("id").GetString() == entry.Target);
            if (target.ValueKind == JsonValueKind.Undefined)
            {
                throw new OwnedTabException("tab_gone", "The tab has closed.");
            }
            using var page = await CdpPage.ConnectAsync(RequiredString(target, "webSocketDebuggerUrl"), (_, _) => { }, token).ConfigureAwait(false);
            void CheckAction()
            {
                Check(caller, entry.Generation, mutate);
                if (!_tabs.TryGetValue(entry.TabId, out var current) || current != entry)
                {
                    throw new OwnedTabException("tab_gone", "The tab has closed.");
                }
            }
            Task<JsonElement> Send(string method, object? parameters) => page.CallAsync(method, parameters, token, CheckAction);
            async Task<JsonElement> Evaluate(string expression)
            {
                var result = await Send("Runtime.evaluate", new { expression, returnByValue = true }).ConfigureAwait(false);
                if (result.TryGetProperty("exceptionDetails", out _))
                {
                    throw new OwnedTabException("invalid_argument", "The selector or page operation failed.");
                }
                return result.GetProperty("result").GetProperty("value");
            }
            switch (action.Tool)
            {
                case "navigate":
                    var navigation = await Send("Page.navigate", new { url = action.Text }).ConfigureAwait(false);
                    if (navigation.TryGetProperty("isDownload", out var download) && download.GetBoolean())
                        throw new OwnedTabException("confirmation_required", "File transfer requires an operator action.");
                    if (navigation.TryGetProperty("errorText", out _))
                    {
                        throw new OwnedTabException(boundary.TakeBlocked(entry.Target) ?? "browser_unavailable", "Navigation failed.");
                    }
                    break;
                case "close":
                    var closed = await Send("Target.closeTarget", new { targetId = entry.Target }).ConfigureAwait(false);
                    if (!closed.GetProperty("success").GetBoolean())
                    {
                        throw new OwnedTabException("browser_unavailable", "The tab could not be closed.");
                    }
                    _tabs.TryRemove(entry.TabId, out _);
                    break;
                case "wait":
                    var expression = action.Selector is { } selector
                        ? "!!document.querySelector(" + JsonSerializer.Serialize(selector) + ")"
                        : loadState == "load" ? "document.readyState === 'complete'" : "document.readyState !== 'loading'";
                    while (!(await Evaluate(expression).ConfigureAwait(false)).GetBoolean())
                    {
                        await Task.Delay(50, token).ConfigureAwait(false);
                    }
                    break;
                case "type":
                    await Send("Input.insertText", new { text = action.Text }).ConfigureAwait(false);
                    break;
                case "key":
                    await ValidateLinkAsync(Evaluate, "document.activeElement").ConfigureAwait(false);
                    var (key, code) = Key(action.Text!);
                    await Send("Input.dispatchKeyEvent", new { type = "keyDown", key, windowsVirtualKeyCode = code, text = key.Length == 1 ? key : "" }).ConfigureAwait(false);
                    await Send("Input.dispatchKeyEvent", new { type = "keyUp", key, windowsVirtualKeyCode = code }).ConfigureAwait(false);
                    break;
                case "scroll":
                    await Send("Input.dispatchMouseEvent", new { type = "mouseWheel", x = 0, y = 0, deltaX = -action.X, deltaY = -action.Y }).ConfigureAwait(false);
                    break;
                case "click":
                    var x = action.X;
                    var y = action.Y;
                    if (action.Selector is { } css)
                    {
                        var point = await Evaluate("(() => { const e = document.querySelector(" + JsonSerializer.Serialize(css)
                            + "); if (!e) return null; e.scrollIntoView({block:'center',inline:'center'}); const r=e.getBoundingClientRect(); return {x:r.x+r.width/2,y:r.y+r.height/2}; })()").ConfigureAwait(false);
                        if (point.ValueKind == JsonValueKind.Null)
                        {
                            throw new OwnedTabException("element_not_found", "No element matches the selector.");
                        }
                        x = point.GetProperty("x").GetDouble();
                        y = point.GetProperty("y").GetDouble();
                    }
                    await ValidateLinkAsync(Evaluate, "document.elementFromPoint(" + JsonSerializer.Serialize(x) + "," + JsonSerializer.Serialize(y) + ")").ConfigureAwait(false);
                    await Send("Input.dispatchMouseEvent", new { type = "mousePressed", x, y, button = "left", clickCount = 1 }).ConfigureAwait(false);
                    await Send("Input.dispatchMouseEvent", new { type = "mouseReleased", x, y, button = "left", clickCount = 1 }).ConfigureAwait(false);
                    break;
            }
            Check(caller, entry.Generation);
            if (action.Tool is "click" or "key" or "navigate")
            {
                // ponytail: feedback waits at most 200 ms; slower boundary events may miss this action's response.
                await Task.Delay(200, token).ConfigureAwait(false);
                if (boundary.TakeBlocked(entry.Target) is { } blocked)
                    throw new OwnedTabException(blocked, "The browser blocked this action at the file or protocol boundary.");
            }
            return new("", true, Action: action.Tool);
        }
        catch (Exception ex) when (IsBrowserFailure(ex))
        {
            return new("", false, Error: Code(ex) + ": " + Message(ex));
        }
    }

    private static void Validate(TabShareRequest action, string? loadState, int timeoutMs)
    {
        var valid = action.Tool switch
        {
            "navigate" => Uri.TryCreate(action.Text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https",
            "click" => action.Selector is { Length: > 0 and <= 2000 } && action.X is null && action.Y is null
                || action.Selector is null && action.X is { } x && double.IsFinite(x) && x >= 0 && action.Y is { } y && double.IsFinite(y) && y >= 0,
            "type" => action.Text is { Length: <= 2000 },
            "key" => action.Text is { } key && Key(key).Code != 0,
            "scroll" => action.X is { } dx && double.IsFinite(dx) && action.Y is { } dy && double.IsFinite(dy),
            "wait" => action.Selector is { Length: > 0 and <= 2000 } && loadState is null
                || action.Selector is null && loadState is "load" or "domcontentloaded",
            "close" => true,
            _ => false,
        };
        if (!valid || timeoutMs is < 1 or > 30_000)
        {
            throw new OwnedTabException("invalid_argument", "Invalid browser action, selector, loadState or timeout (1..30000 ms).");
        }
    }

    private static async Task ValidateLinkAsync(Func<string, Task<JsonElement>> evaluate, string element)
    {
        var href = await evaluate("(() => { const a=(" + element + ")?.closest('a,area'); return a?.href || ''; })()").ConfigureAwait(false);
        if (href.GetString() is { Length: > 0 } url && !AgentTabBoundary.IsWeb(url))
            throw new OwnedTabException("invalid_argument", "Only http(s) links are allowed.");
    }

    private static (string Key, int Code) Key(string key) => key switch
    {
        "Enter" => (key, 13), "Tab" => (key, 9), "Escape" => (key, 27), "Backspace" => (key, 8),
        "Delete" => (key, 46), "ArrowUp" => (key, 38), "ArrowDown" => (key, 40),
        "ArrowLeft" => (key, 37), "ArrowRight" => (key, 39), "PageUp" => (key, 33),
        "PageDown" => (key, 34), "Home" => (key, 36), "End" => (key, 35), "Space" => (" ", 32),
        _ when key.Length == 1 && char.IsAsciiLetter(key[0]) => (key, char.ToUpperInvariant(key[0])),
        _ => (key, 0),
    };
}
