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
    }

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
        browsers.Stop("a");
        Assert.True((await tabs.OpenAsync("a", fixture.Url, "self")).Ok);
        Assert.False((await tabs.ReadAsync("a", aId, "text")).Ok);
        Assert.Single(await tabs.ListAsync("a"));
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
}

public class AppKeyHost : DispatchProxy
{
    private readonly Dictionary<string, object?> _members = new()
    {
        ["get_CurrentMcpCaller"] = null,
        ["get_Channel"] = new AppKeyChannel(),
        ["get_Sessions"] = NullCockpitSessionObserver.Instance,
    };

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
        _members.TryGetValue(targetMethod?.Name ?? "", out var member) ? member : throw new InvalidOperationException("An app key must not reach browser resources.");

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
    public string Root { get; } = Path.Combine("C:\\t\\AC-1870", Guid.NewGuid().ToString("N"));
    public string Url { get; }

    public OwnedTabFixture()
    {
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
                var bytes = Encoding.UTF8.GetBytes("<html><head><title>Local fixture</title></head><body><h1>Fixture content</h1><a href='/'>Local link</a></body></html>");
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
