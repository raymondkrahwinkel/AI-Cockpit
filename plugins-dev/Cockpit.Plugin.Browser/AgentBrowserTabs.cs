using System.Text.Json;

namespace Cockpit.Plugin.Browser;

// AC-1713: What Raymond sees and does in the agent profile, on top of AgentBrowserHost: the tabs of every live agent
// browser, one screencast for the tab he is looking at, and Neem over / Geef terug / Stop agent. The screencast runs
// only between Watch and Unwatch, so nobody looking costs nothing. Frames and changes go out as plugin-channel events.
internal sealed class AgentBrowserTabs(AgentBrowserHost browsers, Func<string, string?> sessionName, Action<string, object> publish) : IDisposable
{
    internal static readonly HashSet<string> InputMethods = new(StringComparer.Ordinal) { "Input.dispatchMouseEvent", "Input.dispatchKeyEvent" };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private readonly object _gate = new();
    private Watch? _watch;

    public async Task<IReadOnlyList<object>> ListAsync(CancellationToken cancellationToken)
    {
        var tabs = new List<object>();
        foreach (var (paneId, upstream, drives) in browsers.Live())
        {
            foreach (var page in await PagesAsync(upstream, cancellationToken).ConfigureAwait(false))
            {
                tabs.Add(new { paneId, session = sessionName(paneId) ?? paneId, targetId = page.Id, title = page.Title, url = page.Url, operatorDrives = drives });
            }
        }

        return tabs;
    }

    // Replaces the screencast in progress: one viewer, one tab.
    public async Task<bool> WatchAsync(string paneId, string targetId, CancellationToken cancellationToken)
    {
        Unwatch();
        var live = browsers.Live().FirstOrDefault(entry => entry.PaneId == paneId);
        if (live.Upstream is null)
        {
            return false;
        }

        var page = (await PagesAsync(live.Upstream, cancellationToken).ConfigureAwait(false)).FirstOrDefault(candidate => candidate.Id == targetId);
        if (page is null)
        {
            return false;
        }

        CdpPage? connection = null;
        connection = await CdpPage.ConnectAsync(page.SocketUrl, (name, parameters) => OnEvent(connection!, paneId, targetId, name, parameters), cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _watch = new Watch(paneId, targetId, connection);
        }

        await connection.SendAsync("Page.startScreencast", new { format = "jpeg", quality = 70, maxWidth = 1280, maxHeight = 1024, everyNthFrame = 1 }).ConfigureAwait(false);
        return true;
    }

    public void Unwatch()
    {
        Watch? watch;
        lock (_gate)
        {
            watch = _watch;
            _watch = null;
        }

        // Closing the socket ends the screencast with it; no frame is asked for or acknowledged after this.
        _ = watch;
    }

    public bool TakeOver(string paneId) => browsers.SetOperatorDrives(paneId, true);

    public bool GiveBack(string paneId) => browsers.SetOperatorDrives(paneId, false);

    public bool Stop(string paneId)
    {
        Unwatch();
        return browsers.StopBrowser(paneId);
    }

    // Raymond's input, only while he drives that session and only into the tab he is watching.
    public async Task<string?> InputAsync(string paneId, string targetId, string method, JsonElement parameters)
    {
        Watch? watch;
        lock (_gate)
        {
            watch = _watch;
        }

        if (InputMethods.Contains(method) == false)
        {
            return "That input is not allowed";
        }

        if (browsers.Live().Any(entry => entry.PaneId == paneId && entry.OperatorDrives) == false)
        {
            return "Take over the agent browser first";
        }

        if (watch is null || watch.PaneId != paneId || watch.TargetId != targetId)
        {
            return "That tab is not the one on screen";
        }

        await watch.Connection.SendAsync(method, parameters).ConfigureAwait(false);
        return null;
    }

    public void Dispose() => Unwatch();

    private void OnEvent(CdpPage connection, string paneId, string targetId, string name, JsonElement parameters)
    {
        if (name != "Page.screencastFrame")
        {
            return;
        }

        var metadata = parameters.GetProperty("metadata");
        publish("agents.frame", new
        {
            paneId,
            targetId,
            data = parameters.GetProperty("data").GetString(),
            width = metadata.GetProperty("deviceWidth").GetDouble(),
            height = metadata.GetProperty("deviceHeight").GetDouble(),
        });

        // The browser sends the next frame only after this one is acknowledged.
        _ = connection.SendAsync("Page.screencastFrameAck", new { sessionId = parameters.GetProperty("sessionId").GetInt32() }).ContinueWith(static _ => { }, TaskScheduler.Default);
    }

    private static async Task<IReadOnlyList<Page>> PagesAsync(string upstream, CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(await Http.GetStringAsync($"{upstream}/json/list", cancellationToken).ConfigureAwait(false));
            return [.. document.RootElement.EnumerateArray()
                .Where(item => item.GetProperty("type").GetString() == "page" && item.TryGetProperty("webSocketDebuggerUrl", out _))
                .Select(item => new Page(item.GetProperty("id").GetString()!, item.GetProperty("title").GetString() ?? string.Empty, item.GetProperty("url").GetString() ?? string.Empty, item.GetProperty("webSocketDebuggerUrl").GetString()!))];
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return [];
        }
    }

    private sealed record Page(string Id, string Title, string Url, string SocketUrl);

    private sealed record Watch(string PaneId, string TargetId, CdpPage Connection);
}
