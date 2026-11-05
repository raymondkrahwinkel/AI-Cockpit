extern alias browserBackend;

using System.Net;
using System.Text.Json;
using BackendBrowserSettings = browserBackend::Cockpit.Plugin.Browser.BrowserSettings;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Browser.Tests;

internal static class AgentNetworkBoundaryScenario
{
    // L2b: real Chromium, both Fetch interception and the socket boundary; no public network dependencies.
    public static async Task RunAsync()
    {
        await using var pageFixture = new OwnedTabFixture();
        await using var destination = new OwnedTabFixture(acceptAnyHost: true);
        var settings = new BackendBrowserSettings(new Storage());
        settings.SaveAgentNetworkAllowlist([new Uri(pageFixture.Url).Authority]);
        var policy = new AddressPolicy(() => settings.AgentNetworkAllowlist);
        using var browsers = pageFixture.Browsers();
        browsers.NetworkPolicy = policy;
        Assert.NotNull(await browsers.GetOrCreateAsync("network-resources"));
        var session = Assert.IsType<AgentBrowserHost.Session>(browsers.SessionFor("network-resources"));
        using var boundary = new AgentTabBoundary(_ => true, _ => { }, policy);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await boundary.StartAsync(session.Upstream, timeout.Token);
        using var http = new HttpClient();
        using var response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Put, session.Upstream + "/json/new?about:blank"), timeout.Token);
        response.EnsureSuccessStatusCode();
        using var target = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        var id = Assert.IsType<string>(target.RootElement.GetProperty("id").GetString());
        await boundary.ReadyAsync(id, timeout.Token);
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var page = await CdpPage.ConnectAsync(Assert.IsType<string>(target.RootElement.GetProperty("webSocketDebuggerUrl").GetString()),
            (name, data) => { _ = name == "Page.loadEventFired" && loaded.TrySetResult(); }, timeout.Token);
        await page.CallAsync("Page.enable", null, timeout.Token);
        Assert.False(await SocketAsync(page, destination.Url, timeout.Token));
        await boundary.PermitAgentNavigationAsync(id, new Uri(pageFixture.Url), timeout.Token);
        await page.CallAsync("Page.navigate", new { url = pageFixture.Url }, timeout.Token);
        await loaded.Task.WaitAsync(timeout.Token);

        Assert.True(await FetchAsync(page, destination.Url, timeout.Token));
        Assert.Null(boundary.TakeBlocked(id));
        Assert.True(await SocketAsync(page, destination.Url, timeout.Token));
        settings.SaveAgentNetworkAllowlist([new Uri(pageFixture.Url).Authority, new Uri(destination.Url).Authority]);
        Assert.True(await FetchAsync(page, destination.Url, timeout.Token));
        Assert.True(await SocketAsync(page, destination.Url, timeout.Token));

        var dnsLookups = 0;
        var rebound = new UriBuilder(destination.Url) { Host = "rebind.invalid" }.Uri;
        browsers.NetworkPolicy = new AddressPolicy(() => settings.AgentNetworkAllowlist, (_, _) =>
        {
            Interlocked.Increment(ref dnsLookups);
            return Task.FromResult(new[] { IPAddress.Loopback });
        });
        Assert.False(await SocketAsync(page, rebound.AbsoluteUri, timeout.Token));
        Assert.Equal(1, dnsLookups);
        settings.SaveAgentNetworkAllowlist([new Uri(pageFixture.Url).Authority, rebound.Authority]);
        Assert.True(await SocketAsync(page, rebound.AbsoluteUri, timeout.Token));
        Assert.Equal(2, dnsLookups);
    }

    private static Task<bool> FetchAsync(CdpPage page, string url, CancellationToken token) => EvaluateAsync(page,
        $"fetch({JsonSerializer.Serialize(url)}, {{cache:'no-store'}}).then(r => r.ok, () => false)", token);

    private static Task<bool> SocketAsync(CdpPage page, string url, CancellationToken token) => EvaluateAsync(page,
        $$"""
        new Promise(resolve => {
            const socket = new WebSocket({{JsonSerializer.Serialize(url.Replace("http:", "ws:", StringComparison.Ordinal) + "socket")}});
            socket.onopen = () => { socket.close(); resolve(true); };
            socket.onerror = () => resolve(false);
        })
        """, token);

    private static async Task<bool> EvaluateAsync(CdpPage page, string expression, CancellationToken token)
    {
        var result = await page.CallAsync("Runtime.evaluate", new { expression, awaitPromise = true, returnByValue = true }, token);
        return result.GetProperty("result").GetProperty("value").GetBoolean();
    }

    private sealed class Storage : IPluginStorage
    {
        private readonly Dictionary<string, object> _entries = new();
        public T? Get<T>(string key) => _entries.TryGetValue(key, out var entry) ? (T)entry : default;
        public void Set<T>(string key, T value) => _entries[key] = value ?? throw new ArgumentNullException(nameof(value));
    }
}
