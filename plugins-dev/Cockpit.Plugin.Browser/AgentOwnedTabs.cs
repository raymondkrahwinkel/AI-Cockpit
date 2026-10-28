using System.Collections.Concurrent;
using System.Text.Json;

namespace Cockpit.Plugin.Browser;

// Public ids never disclose a target or an endpoint. A restarted pane cannot inherit its old run's tabs.
internal sealed class AgentOwnedTabs(AgentBrowserHost browsers)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly ConcurrentDictionary<string, Entry> _tabs = new(StringComparer.Ordinal);
    internal sealed record Entry(string TabId, string Owner, Guid Generation, string Target, string Purpose);
    internal sealed record Info(string TabId, string Title, string Url, string Purpose);
    internal sealed record OpenResult(bool Ok, string? TabId = null, string? Url = null, string? Title = null,
        string? Purpose = null, string Presentation = "hidden", string? Code = null, string? Error = null);

    public static bool IsOwnedId(string id) => id.StartsWith("owned:", StringComparison.Ordinal);

    public async Task<OpenResult> OpenAsync(string? caller, string url, string purpose)
    {
        if (string.IsNullOrEmpty(caller)) return new(false, Code: "not_owner", Error: "A session caller is required.");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || purpose is not ("self" or "operator"))
            return new(false, Code: "invalid_argument", Error: "Use an absolute http(s) URL and purpose self or operator.");

        string? tabId = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            if (await browsers.GetOrCreateAsync(caller, timeout.Token).ConfigureAwait(false) is null)
                return new(false, Code: "browser_unavailable", Error: "The session browser is unavailable.");
            var session = RequireSession(caller);
            Check(caller, session.Generation, mutate: true);
            using var response = await Http.SendAsync(new HttpRequestMessage(HttpMethod.Put, session.Upstream + "/json/new?about:blank"), timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
            var target = created.RootElement.GetProperty("id").GetString()!;
            tabId = "owned:" + Guid.NewGuid().ToString("N");
            _tabs[tabId] = new(tabId, caller, session.Generation, target, purpose);
            var loaded = new ConcurrentDictionary<string, TaskCompletionSource>();
            using var page = await CdpPage.ConnectAsync(created.RootElement.GetProperty("webSocketDebuggerUrl").GetString()!, (name, data) =>
            {
                if (name == "Page.lifecycleEvent" && data.GetProperty("name").GetString() == "DOMContentLoaded")
                    loaded.GetOrAdd(data.GetProperty("loaderId").GetString()!, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
            }, timeout.Token).ConfigureAwait(false);
            await page.CallAsync("Browser.setDownloadBehavior", new { behavior = "deny" }, timeout.Token).ConfigureAwait(false);
            await page.CallAsync("Page.enable", null, timeout.Token).ConfigureAwait(false);
            await page.CallAsync("Page.setLifecycleEventsEnabled", new { enabled = true }, timeout.Token).ConfigureAwait(false);
            Check(caller, session.Generation, mutate: true);
            var navigation = await page.CallAsync("Page.navigate", new { url = uri.AbsoluteUri }, timeout.Token).ConfigureAwait(false);
            if (navigation.TryGetProperty("errorText", out _)) throw new OwnedTabException("browser_unavailable", "Navigation failed.");
            // Match the navigation's loader, so about:blank's readiness cannot complete this open.
            var loader = navigation.GetProperty("loaderId").GetString()!;
            await loaded.GetOrAdd(loader, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            var info = await MetadataAsync(page, timeout.Token).ConfigureAwait(false);
            Check(caller, session.Generation);
            return new(true, tabId, info.Url, info.Title, purpose, purpose == "operator" ? "unavailable" : "hidden");
        }
        catch (Exception ex) when (IsBrowserFailure(ex))
        {
            return new(false, tabId, Purpose: purpose, Code: Code(ex), Error: Message(ex));
        }
    }

    public async Task<IReadOnlyList<Info>> ListAsync(string caller)
    {
        var session = browsers.SessionFor(caller);
        if (session is null) return [];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var pages = JsonDocument.Parse(await Http.GetStringAsync(session.Upstream + "/json/list", timeout.Token).ConfigureAwait(false));
        var result = new List<Info>();
        foreach (var entry in _tabs.Values.Where(e => e.Owner == caller && e.Generation == session.Generation))
        {
            var target = pages.RootElement.EnumerateArray().FirstOrDefault(p => p.GetProperty("id").GetString() == entry.Target);
            if (target.ValueKind != JsonValueKind.Undefined)
                result.Add(new(entry.TabId, target.GetProperty("title").GetString() ?? "", target.GetProperty("url").GetString() ?? "", entry.Purpose));
        }
        Check(caller, session.Generation);
        return result;
    }

    public async Task<TabShareAnswer> ReadAsync(string caller, string tabId, string mode, bool screenshot = false)
    {
        try
        {
            if (mode is not ("text" or "tree")) throw new OwnedTabException("invalid_argument", "Use mode text or tree.");
            if (!_tabs.TryGetValue(tabId, out var entry) || entry.Owner != caller)
                throw new OwnedTabException("not_owner", "This tab does not belong to this session.");
            var session = Check(caller, entry.Generation);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var targets = JsonDocument.Parse(await Http.GetStringAsync(session.Upstream + "/json/list", timeout.Token).ConfigureAwait(false));
            var target = targets.RootElement.EnumerateArray().FirstOrDefault(p => p.GetProperty("id").GetString() == entry.Target);
            if (target.ValueKind == JsonValueKind.Undefined) throw new OwnedTabException("tab_gone", "The tab has closed.");
            using var page = await CdpPage.ConnectAsync(target.GetProperty("webSocketDebuggerUrl").GetString()!, (_, _) => { }, timeout.Token).ConfigureAwait(false);
            Check(caller, entry.Generation);
            var info = await MetadataAsync(page, timeout.Token).ConfigureAwait(false);
            string? text = null;
            string? png = null;
            if (screenshot)
                png = (await page.CallAsync("Page.captureScreenshot", new { format = "png" }, timeout.Token).ConfigureAwait(false)).GetProperty("data").GetString();
            else if (mode == "text")
                text = (await page.CallAsync("Runtime.evaluate", new { expression = "document.body ? document.body.innerText.slice(0,200000) : ''", returnByValue = true }, timeout.Token).ConfigureAwait(false)).GetProperty("result").GetProperty("value").GetString();
            else
            {
                var tree = await page.CallAsync("Accessibility.getFullAXTree", null, timeout.Token).ConfigureAwait(false);
                text = string.Join('\n', tree.GetProperty("nodes").EnumerateArray()
                    .Where(n => !n.GetProperty("ignored").GetBoolean() && n.TryGetProperty("name", out _))
                    .Select(n => n.GetProperty("role").GetProperty("value").GetString() + ": " + n.GetProperty("name").GetProperty("value").GetString()));
                text = text[..Math.Min(text.Length, 200_000)];
            }
            Check(caller, entry.Generation);
            return new("", true, Url: info.Url, Title: info.Title, Text: text, ImagePng: png, TabId: tabId);
        }
        catch (Exception ex) when (IsBrowserFailure(ex)) { return new("", false, Error: Code(ex) + ": " + Message(ex)); }
    }

    private AgentBrowserHost.Session RequireSession(string caller) => browsers.SessionFor(caller)
        ?? throw new OwnedTabException("session_ended", "The browser run has ended.");

    private AgentBrowserHost.Session Check(string caller, Guid generation, bool mutate = false)
    {
        var session = RequireSession(caller);
        if (session.Generation != generation) throw new OwnedTabException("session_ended", "The browser run has ended.");
        if (mutate && session.OperatorDrives) throw new OwnedTabException("operator_drives", "The operator is driving this browser.");
        return session;
    }

    private static async Task<(string Url, string Title)> MetadataAsync(CdpPage page, CancellationToken token)
    {
        var result = await page.CallAsync("Runtime.evaluate", new { expression = "({url:location.href,title:document.title})", returnByValue = true }, token).ConfigureAwait(false);
        var value = result.GetProperty("result").GetProperty("value");
        return (value.GetProperty("url").GetString() ?? "", value.GetProperty("title").GetString() ?? "");
    }

    internal static bool IsBrowserFailure(Exception ex) => ex is OwnedTabException or IOException or HttpRequestException or OperationCanceledException or System.Net.WebSockets.WebSocketException or ObjectDisposedException;
    internal static string Code(Exception ex) => ex is OwnedTabException owned ? owned.Code : ex is OperationCanceledException ? "timeout" : "browser_unavailable";
    internal static string Message(Exception ex) => ex is OwnedTabException ? ex.Message : "The browser operation could not complete.";
    private sealed class OwnedTabException(string code, string message) : Exception(message) { public string Code { get; } = code; }
}
