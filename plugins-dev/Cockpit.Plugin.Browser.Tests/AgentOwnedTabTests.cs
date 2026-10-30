using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Reflection;
using System.Text.Json;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.Browser.Tests;

public class AgentOwnedTabTests
{
    // L2b: real Chromium, local HTTP fixture; concurrent opens preserve the first target and its content.
    [Fact]
    public async Task TwoSessions_OpenListReadAndCapture_WithoutReplacingTheirTabs()
    {
        await using var fixture = new OwnedTabFixture();
        using var browsers = fixture.Browsers();
        var tabs = new AgentOwnedTabs(browsers);
        var a = await tabs.OpenAsync("a", fixture.Url, "self");
        Assert.True(a.Ok, a.Error);
        var aId = Assert.IsType<string>(a.TabId);
        Assert.Equal("Local fixture", a.Title);
        Assert.Equal("hidden", a.Presentation);
        var more = await Task.WhenAll(tabs.OpenAsync("a", fixture.Url, "operator"), tabs.OpenAsync("a", fixture.Url, "self"), tabs.OpenAsync("b", fixture.Url, "self"));
        Assert.All(more, tab => Assert.True(tab.Ok, tab.Error));
        var bId = Assert.IsType<string>(more[2].TabId);
        Assert.Equal("unavailable", more[0].Presentation);
        Assert.Equal(3, (await tabs.ListAsync("a")).Count);
        Assert.Equal(more[2].TabId, Assert.Single(await tabs.ListAsync("b")).TabId);
        Assert.DoesNotContain(await tabs.ListAsync("a"), tab => tab.TabId == more[2].TabId);
        var text = await tabs.ReadAsync("a", aId, "text");
        Assert.True(text.Ok, text.Error);
        Assert.Equal("Local fixture", text.Title);
        Assert.Equal(fixture.Url, text.Url);
        Assert.Contains("Fixture content", text.Text);
        var tree = await tabs.ReadAsync("a", aId, "tree");
        Assert.True(tree.Ok, tree.Error);
        Assert.Contains("heading: Fixture content", tree.Text);
        var image = await tabs.ReadAsync("a", aId, "text", screenshot: true);
        Assert.True(image.Ok, image.Error);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, Convert.FromBase64String(Assert.IsType<string>(image.ImagePng))[..8]);
        Assert.Contains("Fixture content", (await tabs.ReadAsync("b", bId, "text")).Text);
        Assert.Contains("heading: Fixture content", (await tabs.ReadAsync("b", bId, "tree")).Text);
        Assert.NotEmpty(Assert.IsType<string>((await tabs.ReadAsync("b", bId, "text", screenshot: true)).ImagePng));
        await using var second = new OwnedTabFixture();
        var host = DispatchProxy.Create<ICockpitHost, AppKeyHost>();
        ((AppKeyHost)(object)host).Caller = "a";
        using var bridge = new TabShareBridge(host);
        var tools = new TabShareMcpTools(host, bridge, tabs);
        AssertOk(await tools.Navigate(aId, second.Url));
        AssertOk(await tools.WaitFor(aId, loadState: "domcontentloaded"));
        AssertOk(await tools.WaitFor(aId, selector: "#field"));
        AssertOk(await tools.Click(aId, selector: "#field"));
        AssertOk(await tools.Type(aId, "typed fixture"));
        Assert.Contains("typed fixture", (await tabs.ReadAsync("a", aId, "text")).Text);
        AssertOk(await tools.PressKey(aId, "Enter"));
        AssertOk(await tools.WaitFor(aId, selector: "#entered"));
        AssertOk(await tools.Scroll(aId, 0, -120));
        AssertOk(await tools.Click(aId, 10, 10));
        AssertOk(await tools.Click(aId, selector: "#link"));
        AssertOk(await tools.WaitFor(aId, loadState: "load"));
        Assert.Equal(second.Url, (await tabs.ReadAsync("a", aId, "text")).Url);
        Assert.Contains("timeout:", await tools.WaitFor(aId, selector: "#never", timeoutMs: 150));
        Assert.Contains("invalid_argument:", await tools.WaitFor(aId, selector: "["));
        Assert.Contains("invalid_argument:", await tools.WaitFor(aId, selector: "body", timeoutMs: 30001));
        Assert.Contains("invalid_argument:", await tools.Navigate(aId, "file:///secret"));
        Assert.Contains("not_owner:", await tools.CloseTab(bId));
        AssertOk(await tools.CloseTab(aId));
        Assert.DoesNotContain(await tabs.ListAsync("a"), tab => tab.TabId == aId);
        Assert.Contains("not_owner:", await tools.WaitFor(aId, selector: "body"));
        await DownloadBoundaryAsync(tabs, browsers, fixture, "self");
        await DownloadBoundaryAsync(tabs, browsers, fixture, "operator");
        await ChooserBoundaryAsync(tabs, browsers, fixture, "self");
        await ChooserBoundaryAsync(tabs, browsers, fixture, "operator");
        await ProtocolBoundaryAsync(tabs, fixture, "self");
        await ProtocolBoundaryAsync(tabs, fixture, "operator");
    }

    private static async Task DownloadBoundaryAsync(AgentOwnedTabs tabs, AgentBrowserHost browsers, OwnedTabFixture fixture, string purpose)
    {
        var opened = await tabs.OpenAsync("boundary", fixture.Url, purpose);
        Assert.True(opened.Ok, opened.Error);
        using var page = await FixturePageAsync(browsers, fixture.Url);
        await page.CallAsync("Browser.setDownloadBehavior", new { behavior = "allow", downloadPath = fixture.Downloads }, CancellationToken.None);
        var download = await tabs.OpenAsync("boundary", fixture.Url + "download", purpose);
        await Task.Delay(200);
        Assert.Empty(Directory.GetFiles(fixture.Downloads));
        Assert.False(download.Ok);
        Assert.Equal("confirmation_required", download.Code);
        Assert.Contains("confirmation_required", (await tabs.ActAsync("boundary", new("", "", "click", opened.TabId, Selector: "#download"))).Error);
        Assert.Contains("confirmation_required", (await tabs.ActAsync("boundary", new("", "", "click", opened.TabId, Selector: "#download-popup"))).Error);
        Assert.Equal("confirmation_required", (await tabs.OpenAsync("boundary", fixture.Url + "redirect?to=/download", purpose)).Code);
        Assert.Empty(Directory.GetFiles(fixture.Downloads));
    }

    private static async Task ChooserBoundaryAsync(AgentOwnedTabs tabs, AgentBrowserHost browsers, OwnedTabFixture fixture, string purpose)
    {
        var opened = await tabs.OpenAsync("boundary", fixture.Url, purpose);
        Assert.True(opened.Ok, opened.Error);
        using var page = await FixturePageAsync(browsers, fixture.Url);
        var clicked = await tabs.ActAsync("boundary", new("", "", "click", opened.TabId, Selector: "#upload"));
        Assert.Contains("confirmation_required", clicked.Error);
        var files = await page.CallAsync("Runtime.evaluate", new { expression = "document.querySelector('#upload').files.length", returnByValue = true }, CancellationToken.None);
        Assert.Equal(0, files.GetProperty("result").GetProperty("value").GetInt32());
    }

    private static async Task ProtocolBoundaryAsync(AgentOwnedTabs tabs, OwnedTabFixture fixture, string purpose)
    {
        Assert.Equal("invalid_argument", (await tabs.OpenAsync("boundary", "javascript:document.title='escaped'", purpose)).Code);
        Assert.Equal("invalid_argument", (await tabs.OpenAsync("boundary", "file:///secret", purpose)).Code);
        Assert.Equal("invalid_argument", (await tabs.OpenAsync("boundary", "chrome://version", purpose)).Code);
        Assert.Equal("invalid_argument", (await tabs.OpenAsync("boundary", fixture.Url + "redirect?to=data:text/html,secret", purpose)).Code);
        Assert.Equal("invalid_argument", (await tabs.OpenAsync("boundary", fixture.Url + "redirect?to=file:///secret", purpose)).Code);
        var opened = await tabs.OpenAsync("boundary", fixture.Url, purpose);
        Assert.True(opened.Ok, opened.Error);
        Assert.Contains("invalid_argument", (await tabs.ActAsync("boundary", new("", "", "click", opened.TabId, Selector: "#script"))).Error);
        Assert.Contains("invalid_argument", (await tabs.ActAsync("boundary", new("", "", "click", opened.TabId, Selector: "#internal"))).Error);
        Assert.Contains("invalid_argument", (await tabs.ActAsync("boundary", new("", "", "click", opened.TabId, Selector: "#bad-popup"), timeoutMs: 5000)).Error);
        Assert.Contains("invalid_argument", (await tabs.ActAsync("boundary", new("", "", "navigate", opened.TabId, Text: "data:text/html,secret"))).Error);
        Assert.True((await tabs.ActAsync("boundary", new("", "", "click", opened.TabId, Selector: "#popup"))).Ok);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        AgentOwnedTabs.Info? popup;
        do
        {
            popup = (await tabs.ListAsync("boundary")).FirstOrDefault(t => t.Url == fixture.Url + "popup");
            if (popup is null) await Task.Delay(50, timeout.Token);
        } while (popup is null);
        Assert.Equal("self", popup.Purpose);
        Assert.Contains("Fixture content", (await tabs.ReadAsync("boundary", popup.TabId, "text")).Text);
        Assert.True((await tabs.ActAsync("boundary", new("", "", "close", popup.TabId))).Ok);
    }

    private static async Task<string> SocketAsync(AgentBrowserHost browsers, string url)
    {
        using var http = new HttpClient();
        using var targets = JsonDocument.Parse(await http.GetStringAsync(browsers.SessionFor("boundary")!.Upstream + "/json/list"));
        return targets.RootElement.EnumerateArray().First(t => t.GetProperty("url").GetString() == url).GetProperty("webSocketDebuggerUrl").GetString()!;
    }

    private static async Task<CdpPage> FixturePageAsync(AgentBrowserHost browsers, string url) =>
        await CdpPage.ConnectAsync(await SocketAsync(browsers, url), (_, _) => { }, CancellationToken.None);

    // L2b: caller and generation boundaries, takeover, validation and failed starts leave no usable browser.
    [Fact]
    public async Task ForeignMissingAndPreviousRunCallers_AreRefused_AndFailedStartsLeaveNoProfile()
    {
        await using var fixture = new OwnedTabFixture();
        using var browsers = fixture.Browsers();
        var tabs = new AgentOwnedTabs(browsers);
        var host = DispatchProxy.Create<ICockpitHost, AppKeyHost>();
        using var bridge = new TabShareBridge(host);
        var tools = new TabShareMcpTools(host, bridge, tabs);
        Assert.False(JsonDocument.Parse(await tools.OpenTab(fixture.Url, "self")).RootElement.GetProperty("ok").GetBoolean());
        Assert.False(JsonDocument.Parse(await tools.ListTabs()).RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("not_owner", (await tabs.OpenAsync(null, fixture.Url, "self")).Code);
        Assert.Empty(browsers.Live());
        Assert.Equal("invalid_argument", (await tabs.OpenAsync("a", "file:///secret", "self")).Code);
        Assert.Equal("invalid_argument", (await tabs.OpenAsync("a", fixture.Url, "other")).Code);
        var a = await tabs.OpenAsync("a", fixture.Url, "self");
        var b = await tabs.OpenAsync("b", fixture.Url, "self");
        Assert.True(a.Ok, a.Error);
        Assert.True(b.Ok, b.Error);
        var aId = Assert.IsType<string>(a.TabId);
        Assert.False((await tabs.ReadAsync("b", aId, "text")).Ok);
        Assert.False((await tabs.ReadAsync("b", aId, "text", screenshot: true)).Ok);
        Assert.False((await tabs.ReadAsync("a", "owned:unknown", "text")).Ok);
        Assert.False((await tabs.ReadAsync("a", aId, "invalid")).Ok);
        Assert.True(browsers.SetOperatorDrives("a", true));
        Assert.Equal("operator_drives", (await tabs.OpenAsync("a", fixture.Url, "self")).Code);
        Assert.True((await tabs.ReadAsync("a", aId, "text")).Ok);
        Assert.Contains("operator_drives:", (await tabs.ActAsync("a", new("", "", "navigate", aId, Text: fixture.Url))).Error);
        Assert.Contains("operator_drives:", (await tabs.ActAsync("a", new("", "", "close", aId))).Error);
        Assert.Contains("operator_drives:", (await tabs.ActAsync("a", new("", "", "click", aId, Selector: "body"))).Error);
        Assert.Contains("operator_drives:", (await tabs.ActAsync("a", new("", "", "type", aId, Text: "blocked"))).Error);
        Assert.Contains("operator_drives:", (await tabs.ActAsync("a", new("", "", "key", aId, Text: "Enter"))).Error);
        Assert.Contains("operator_drives:", (await tabs.ActAsync("a", new("", "", "scroll", aId, X: 0, Y: -120))).Error);
        Assert.True((await tabs.ActAsync("a", new("", "", "wait", aId, Selector: "body"))).Ok);
        browsers.SetOperatorDrives("a", false);
        var pending = tabs.ActAsync("a", new("", "", "wait", aId, Selector: "#never"), timeoutMs: 5000);
        browsers.Stop("a");
        Assert.False((await pending).Ok);
        Assert.True((await tabs.OpenAsync("a", fixture.Url, "self")).Ok);
        Assert.False((await tabs.ReadAsync("a", aId, "text")).Ok);
        Assert.Contains("session_ended:", (await tabs.ActAsync("a", new("", "", "close", aId))).Error);
        Assert.Single(await tabs.ListAsync("a"));
        // AC-1884: a failed open returns no tabId and leaves no about:blank target behind.
        using (var free = new TcpListener(IPAddress.Loopback, 0))
        {
            free.Start();
            var dead = $"http://127.0.0.1:{((IPEndPoint)free.LocalEndpoint).Port}/";
            free.Stop();
            var failedOpen = await tabs.OpenAsync("a", dead, "self");
            Assert.False(failedOpen.Ok);
            Assert.Null(failedOpen.TabId);
            Assert.Equal("browser_unavailable", failedOpen.Code);
            Assert.Single(await tabs.ListAsync("a"));
        }
        using var failed = new AgentBrowserHost(() => "C:\\t\\AC-1870\\missing-browser.exe", fixture.Root + "-failed");
        Assert.Null(await failed.GetOrCreateAsync("failed"));
        Assert.Null(await failed.GetOrCreateAsync("failed"));
        Assert.Empty(failed.Live());
        Assert.Empty(Directory.GetDirectories(fixture.Root + "-failed"));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var resolutions = 0;
        using var queued = new AgentBrowserHost(() =>
        {
            Interlocked.Increment(ref resolutions);
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            return null;
        }, fixture.Root + "-queued");
        var firstStart = Task.Run(() => queued.GetOrCreateAsync("starting"));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        var endedStart = queued.GetOrCreateAsync("ended");
        queued.Stop("ended");
        release.Set();
        Assert.Null(await firstStart);
        Assert.Null(await endedStart);
        Assert.Equal(1, resolutions);
        Assert.Null(queued.SessionFor("ended"));
    }

    private static void AssertOk(string result)
    {
        using var json = JsonDocument.Parse(result);
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean(), result);
    }
}

public class AppKeyHost : DispatchProxy
{
    public string? Caller { get; set; }
    private readonly Dictionary<string, object?> _members = new()
    {
        ["get_CurrentMcpCaller"] = null,
        ["get_Channel"] = new AppKeyChannel(),
        ["get_Sessions"] = NullCockpitSessionObserver.Instance,
    };

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
        targetMethod?.Name == "get_CurrentMcpCaller" && Caller is { } caller
            ? new Cockpit.Plugins.Abstractions.Mcp.McpCaller(caller, Cockpit.Plugins.Abstractions.Mcp.McpCallerKind.Session)
            : _members.TryGetValue(targetMethod?.Name ?? "", out var member) ? member : throw new InvalidOperationException("An app key must not reach browser resources.");

    private sealed class AppKeyChannel : IPluginBackendChannel, IDisposable
    {
        public IDisposable Handle(string action, Func<JsonElement, CancellationToken, Task<JsonElement>> handler) => this;
        public void Publish(string name, JsonElement payload) => throw new InvalidOperationException("An app key must not dispatch browser requests.");
        public void Dispose() { }
    }
}

// A loopback-only server; no production URLs, downloads or runtime fetches.
internal sealed class OwnedTabFixture : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Task _server;
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "cockpit-owned-tabs", Guid.NewGuid().ToString("N"));
    public string Downloads => Path.Combine(Root, "downloads");
    public string Url { get; }

    public OwnedTabFixture()
    {
        Directory.CreateDirectory(Downloads);
        using var port = new TcpListener(IPAddress.Loopback, 0);
        port.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)port.LocalEndpoint).Port}/";
        port.Stop();
        _listener.Prefixes.Add(Url);
        _listener.Start();
        _server = ServeAsync();
    }

    public AgentBrowserHost Browsers() => new(() => AgentBrowserRuntime.SystemBrowser()
        ?? throw new InvalidOperationException("Install Chrome or Edge for this local fixture."), Root);

    private async Task ServeAsync()
    {
        try
        {
            while (_listener.IsListening)
            {
                var context = await _listener.GetContextAsync();
                if (context.Request.Url!.AbsolutePath == "/redirect")
                {
                    context.Response.StatusCode = 302;
                    context.Response.RedirectLocation = context.Request.QueryString["to"];
                    context.Response.Close();
                    continue;
                }
                if (context.Request.Url!.AbsolutePath == "/download")
                {
                    context.Response.ContentType = "application/octet-stream";
                    context.Response.Headers["Content-Disposition"] = "attachment; filename=boundary.txt";
                    await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("fixture download"));
                    context.Response.Close();
                    continue;
                }
                var bytes = Encoding.UTF8.GetBytes("""
                    <html><head><title>Local fixture</title></head><body><h1>Fixture content</h1>
                    <a id='link' href='/'>Local link</a>
                    <input id='field' oninput='document.querySelector("output").textContent=this.value' onkeydown='if(event.key==="Enter")this.id="entered"'><output></output>
                    <a id='download' href='/download'>download</a><a id='download-popup' target='_blank' href='/download'>download popup</a>
                    <input id='upload' type='file'><a id='script' href='javascript:document.title="escaped"'>script</a>
                    <a id='internal' href='chrome://version'>internal</a><a id='popup' target='_blank' href='/popup'>popup</a>
                    <button id='bad-popup' onclick='window.open("data:text/html,secret")'>bad popup</button>
                    </body></html>
                    """);
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Close();
        await _server.ConfigureAwait(false);
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, true);
        }
        if (Directory.Exists(Root + "-failed"))
        {
            Directory.Delete(Root + "-failed", true);
        }
        if (Directory.Exists(Root + "-queued"))
        {
            Directory.Delete(Root + "-queued", true);
        }
    }
}
