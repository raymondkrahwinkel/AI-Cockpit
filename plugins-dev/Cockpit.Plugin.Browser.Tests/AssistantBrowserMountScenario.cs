using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using Cockpit.Core.Assistant;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Assistant;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Cockpit.Plugin.Browser.Tests;

// L2: the assistant uses the real mount door, transport identity and run-end observer.
internal static class AssistantBrowserMountScenario
{
    public static async Task RunAsync()
    {
        await using var fixture = new OwnedTabFixture();
        using var browsers = fixture.Browsers();
        var keyring = new SessionMcpKeyring();
        var mounts = new SessionMcpMounts();
        using var services = new ServiceCollection().AddSingleton(keyring).BuildServiceProvider();
        var observer = new PluginBackendSessionObserver(new SessionRegistry(), services);
        observer.SessionClosed += (_, pane) => browsers.Stop(pane);
        var pluginHost = DispatchProxy.Create<ICockpitHost, TransportBrowserHost>();
        using var bridge = new TabShareBridge(pluginHost);
        var tools = new TabShareMcpTools(pluginHost, bridge,
            new AgentOwnedTabs(browsers, new AddressPolicy(() => ["127.0.0.0/8"])));
        await using var endpoint = new CockpitMcpEndpointHost([], services, new McpAuthKey(), keyring,
            new NodeEndpointSettingsStore(Path.Combine(fixture.Root, "node.json")),
            new NodeSelfSignedCertificate(Path.Combine(fixture.Root, "node.pfx")), mounts,
            NullLoggerFactory.Instance, new NodeListenAddress(IPAddress.Loopback));
        var enabled = true;
        await endpoint.MountAsync("cockpit-browser", tools, () => enabled);
        var url = Assert.IsType<string>(Assert.Single(endpoint.GetServers()).Url);
        var profile = new SessionProfile("assistant", new ClaudeConfig("unused"))
        {
            EnabledMcpServerNames = ["cockpit-browser"],
        };
        var selection = AssistantSessionHost.McpSelection(profile, []);
        mounts.Grant(AssistantIdentity.PaneId, [.. selection]);
        var token = keyring.TokenFor(AssistantIdentity.PaneId);
        await using var client = await ConnectAsync(url, token);
        using var opened = await CallAsync(client, "browser_open_tab", new() { ["url"] = fixture.Url, ["purpose"] = "self" });
        Assert.True(opened.RootElement.GetProperty("ok").GetBoolean(), opened.RootElement.ToString());
        var tabId = Assert.IsType<string>(opened.RootElement.GetProperty("tabId").GetString());
        using var read = await CallAsync(client, "browser_read_tab", new() { ["tabId"] = tabId });
        Assert.Equal("Local fixture", read.RootElement.GetProperty("title").GetString());
        using var shown = await CallAsync(client, "browser_open_tab", new() { ["url"] = fixture.Url, ["purpose"] = "operator" });
        Assert.True(shown.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("unavailable", shown.RootElement.GetProperty("presentation").GetString());
        var originalSession = browsers.SessionFor(AssistantIdentity.PaneId);
        Assert.NotNull(originalSession);
        var generation = originalSession.Generation;
        var replacementToken = keyring.TokenFor(AssistantIdentity.PaneId);
        Assert.Null(keyring.PaneFor(token));
        Assert.Null(browsers.SessionFor(AssistantIdentity.PaneId));
        await using var replacement = await ConnectAsync(url, replacementToken);
        using var stale = await CallAsync(replacement, "browser_read_tab", new() { ["tabId"] = tabId });
        Assert.False(stale.RootElement.GetProperty("ok").GetBoolean());
        using var fresh = await CallAsync(replacement, "browser_open_tab", new() { ["url"] = fixture.Url, ["purpose"] = "self" });
        Assert.True(fresh.RootElement.GetProperty("ok").GetBoolean());
        var freshSession = browsers.SessionFor(AssistantIdentity.PaneId);
        Assert.NotNull(freshSession);
        Assert.NotEqual(generation, freshSession.Generation);
        keyring.Revoke(AssistantIdentity.PaneId, replacementToken);
        Assert.Null(browsers.SessionFor(AssistantIdentity.PaneId));

        // The negative scenario sends a valid assistant bearer directly, bypassing client selection.
        mounts.Grant(AssistantIdentity.PaneId, [.. AssistantSessionHost.McpSelection(profile with { EnabledMcpServerNames = [] }, [])]);
        var unmountedToken = keyring.TokenFor(AssistantIdentity.PaneId);
        Assert.Equal(HttpStatusCode.Forbidden, await ProbeAsync(url, unmountedToken, fixture.Url));
        Assert.Null(browsers.SessionFor(AssistantIdentity.PaneId));
        mounts.Grant(AssistantIdentity.PaneId, [.. selection]);
        enabled = false;
        Assert.Equal(HttpStatusCode.Forbidden, await ProbeAsync(url, unmountedToken, fixture.Url));
        Assert.Null(browsers.SessionFor(AssistantIdentity.PaneId));
        keyring.Revoke(AssistantIdentity.PaneId, unmountedToken);
    }

    private static Task<McpClient> ConnectAsync(string url, string token) => McpClient.CreateAsync(
        new HttpClientTransport(new HttpClientTransportOptions
        {
            Name = "cockpit-browser",
            Endpoint = new Uri(url),
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" },
        }));

    private static async Task<JsonDocument> CallAsync(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(tool, arguments);
        return JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }

    private static async Task<HttpStatusCode> ProbeAsync(string url, string token, string pageUrl)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var body = new StringContent(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", id = 1, method = "tools/call",
            @params = new { name = "browser_open_tab", arguments = new { url = pageUrl, purpose = "self" } },
        }), System.Text.Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(url, body);
        return response.StatusCode;
    }
}

