using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Reflection;
using System.Text.Json;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Browser.Tests;

public class AgentOwnedTabTests
{
    // L2b: real Chromium, local HTTP fixture; concurrent opens preserve the first target and its content.
    [Fact]
    public async Task TwoSessions_OpenListReadAndCapture_WithoutReplacingTheirTabs()
    {
        using var fixture = new OwnedTabFixture();
        using var browsers = fixture.Browsers();
        var tabs = new AgentOwnedTabs(browsers);
        var a = await tabs.OpenAsync("a", fixture.Url, "self");
        Assert.True(a.Ok, a.Error);
        Assert.Equal("Local fixture", a.Title);
        Assert.Equal("hidden", a.Presentation);
        var more = await Task.WhenAll(tabs.OpenAsync("a", fixture.Url, "operator"), tabs.OpenAsync("b", fixture.Url, "self"));
        Assert.All(more, tab => Assert.True(tab.Ok, tab.Error));
        Assert.Equal("unavailable", more[0].Presentation);
        Assert.Equal(2, (await tabs.ListAsync("a")).Count);
        Assert.Equal(more[1].TabId, Assert.Single(await tabs.ListAsync("b")).TabId);
        Assert.DoesNotContain(await tabs.ListAsync("a"), tab => tab.TabId == more[1].TabId);
        var text = await tabs.ReadAsync("a", a.TabId!, "text");
        Assert.True(text.Ok, text.Error);
        Assert.Equal("Local fixture", text.Title);
        Assert.Equal(fixture.Url, text.Url);
        Assert.Contains("Fixture content", text.Text);
        var tree = await tabs.ReadAsync("a", a.TabId!, "tree");
        Assert.True(tree.Ok, tree.Error);
        Assert.Contains("heading: Fixture content", tree.Text);
        var image = await tabs.ReadAsync("a", a.TabId!, "text", screenshot: true);
        Assert.True(image.Ok, image.Error);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, Convert.FromBase64String(image.ImagePng!)[..8]);
        Assert.Contains("Fixture content", (await tabs.ReadAsync("b", more[1].TabId!, "text")).Text);
        Assert.Contains("heading: Fixture content", (await tabs.ReadAsync("b", more[1].TabId!, "tree")).Text);
        Assert.NotEmpty((await tabs.ReadAsync("b", more[1].TabId!, "text", screenshot: true)).ImagePng!);
    }

    // L2b: caller and generation boundaries, takeover, validation and failed starts leave no usable browser.
    [Fact]
    public async Task ForeignMissingAndPreviousRunCallers_AreRefused_AndFailedStartsLeaveNoProfile()
    {
        using var fixture = new OwnedTabFixture();
        using var browsers = fixture.Browsers();
        var tabs = new AgentOwnedTabs(browsers);
        var tools = new TabShareMcpTools(DispatchProxy.Create<ICockpitHost, AppKeyHost>(), null!, tabs);
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
        Assert.False((await tabs.ReadAsync("b", a.TabId!, "text")).Ok);
        Assert.False((await tabs.ReadAsync("b", a.TabId!, "text", screenshot: true)).Ok);
        Assert.False((await tabs.ReadAsync("a", "owned:unknown", "text")).Ok);
        Assert.False((await tabs.ReadAsync("a", a.TabId!, "invalid")).Ok);
        Assert.True(browsers.SetOperatorDrives("a", true));
        Assert.Equal("operator_drives", (await tabs.OpenAsync("a", fixture.Url, "self")).Code);
        Assert.True((await tabs.ReadAsync("a", a.TabId!, "text")).Ok);
        browsers.Stop("a");
        Assert.True((await tabs.OpenAsync("a", fixture.Url, "self")).Ok);
        Assert.False((await tabs.ReadAsync("a", a.TabId!, "text")).Ok);
        Assert.Single(await tabs.ListAsync("a"));
        using var failed = new AgentBrowserHost(() => "C:\\t\\AC-1870\\missing-browser.exe", fixture.Root + "-failed");
        Assert.Null(await failed.GetOrCreateAsync("failed"));
        Assert.Null(await failed.GetOrCreateAsync("failed"));
        Assert.Empty(failed.Live());
        Assert.Empty(Directory.GetDirectories(fixture.Root + "-failed"));
    }
}

public class AppKeyHost : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
        targetMethod?.Name == "get_CurrentMcpCaller" ? null : throw new InvalidOperationException("An app key must not reach browser resources.");
}

// A loopback-only server; no production URLs, downloads or runtime fetches.
internal sealed class OwnedTabFixture : IDisposable
{
    private readonly HttpListener _listener = new();
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
        _ = ServeAsync();
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

    public void Dispose()
    {
        _listener.Close();
        if (Directory.Exists(Root)) Directory.Delete(Root, true);
        if (Directory.Exists(Root + "-failed")) Directory.Delete(Root + "-failed", true);
    }
}
