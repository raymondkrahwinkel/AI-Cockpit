using System.Text.Json;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Permissions;
using Microsoft.Extensions.AI;
using Cockpit.Infrastructure.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NSubstitute;

namespace Cockpit.Core.Tests.Mcp;

public partial class McpClientConnectorTests
{
    // Category E: folding and bounded gateway names must remain unique and reversible (AC-2128).
    private static void _AssertToolNameRules()
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        Assert.Equal("YouTrack__Personal", McpToolNames.Alias("YouTrack: Personal", used));
        Assert.Equal("YouTrack__Personal_2", McpToolNames.Alias("YouTrack? Personal", used));
        var names = new McpToolNames();
        var exposed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var canonical in new[]
        {
            "mcp__YouTrack__Personal__get_issue",
            "mcp__Depot__depot_krahwinkel-it_nl__get_self_registration",
            "mcp__" + new string('a', 70) + "__read",
            "mcp__" + new string('a', 70) + "__write",
        })
        {
            var gateway = names.ToGateway(canonical);
            Assert.True(gateway.Length <= 64);
            Assert.True(exposed.Add(gateway));
            Assert.Equal(canonical, names.ToCanonical(gateway));
            Assert.Equal(gateway, names.ToGateway(canonical));
        }
        var first = names.ForTool("a b", "read");
        var second = names.ForTool("a:b", "read");
        Assert.NotEqual(first, second);
        Assert.Equal(first, names.ForTool("a b", "read"));
        Assert.Equal("mcp__a_b__read", names.ToCanonical("mcp__cockpit__" + first));
        Assert.Equal("mcp__a_b__read", names.ToCanonical(first));
        Assert.Equal(first, names.InBrief("mcp__a_b__read", localTools: true));
        Assert.Equal("mcp__a_b_2__read", names.ToCanonical("mcp__cockpit__" + second));
        Assert.Equal("Read", names.ToCanonical("Read"));
        Assert.Equal("mcp__depot__read", names.ToCanonical("mcp__depot__read"));
        var boundary = "mcp__" + new string('x', 50);
        Assert.Equal(64, names.ToGateway(boundary).Length);
        Assert.Equal("mcp__cockpit__" + boundary[5..], names.ToGateway(boundary));
    }

    // Category D: the wire contract must retain image blocks and tool metadata through the gateway.
    private static async Task _AssertGatewayContractAsync(InProcessMcpHttpServer upstream, McpClient direct)
    {
        _AssertToolNameRules();
        var config = new McpServerConfig { Name = "depot", Transport = McpTransport.Http, Url = upstream.Url };
        var registry = new List<McpServerConfig> { config };
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersForProjectAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(registry);
        var keyring = new SessionMcpKeyring();
        var auth = new McpAuthKey();
        var mounts = new SessionMcpMounts();
        mounts.Bind("contract", new SessionMcpContext(null, "", null, "agent"));
        mounts.Grant("contract", ["depot"]);
        var token = keyring.TokenFor("contract");
        var provider = new McpToolProvider(catalog, Substitute.For<IMcpOAuthAuthorizer>(), Substitute.For<IMcpOAuthCoordinator>(), auth, keyring, NullLogger<McpToolProvider>.Instance, mounts: mounts);
        var directory = Path.Combine(Path.GetTempPath(), $"gateway-contract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "cockpit.json");
            await using var gateway = new SessionMcpGatewayHost(auth, keyring, NullLoggerFactory.Instance, path, provider, mounts);
            await gateway.StartAsync(CancellationToken.None);
            await using var client = await McpClient.CreateAsync(new HttpClientTransport(new()
            {
                Endpoint = new Uri(gateway.UrlFor("contract") ?? throw new InvalidOperationException("Gateway unavailable")),
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" },
            }));
            var original = Assert.Single(await direct.ListToolsAsync());
            var forwarded = Assert.Single(await client.ListToolsAsync());
            Assert.Equal("depot__contract", forwarded.Name);
            var expectedTool = JsonSerializer.SerializeToNode(original.ProtocolTool);
            var actualTool = JsonSerializer.SerializeToNode(forwarded.ProtocolTool);
            Assert.NotNull(expectedTool);
            Assert.NotNull(actualTool);
            expectedTool["name"] = forwarded.Name;
            Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(expectedTool, actualTool));
            var expected = await direct.CallToolAsync(original.Name);
            var actual = await client.CallToolAsync(forwarded.Name);
            Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(JsonSerializer.SerializeToNode(expected), JsonSerializer.SerializeToNode(actual)));
            Assert.IsType<ImageContentBlock>(actual.Content[1]);
            Assert.Equal(token, keyring.CurrentToken("contract"));

            using var http = new HttpClient();
            using var initialize = new HttpRequestMessage(HttpMethod.Post, gateway.UrlFor("contract"));
            initialize.Headers.Authorization = new("Bearer", token);
            initialize.Headers.Accept.ParseAdd("application/json, text/event-stream");
            // AC-2163: termination must also work after the initialize request's context is disposed.
            initialize.Headers.ConnectionClose = true;
            initialize.Content = System.Net.Http.Json.JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "termination-contract", version = "1" } } });
            using var initialized = await http.SendAsync(initialize);
            initialized.EnsureSuccessStatusCode();
            string sessionId = initialized.Headers.GetValues("Mcp-Session-Id").Single();
            using var ready = new HttpRequestMessage(HttpMethod.Post, gateway.UrlFor("contract"));
            ready.Headers.Authorization = new("Bearer", token);
            ready.Headers.Accept.ParseAdd("application/json, text/event-stream");
            ready.Headers.Add("Mcp-Session-Id", sessionId);
            ready.Headers.Add("MCP-Protocol-Version", "2025-11-25");
            ready.Content = System.Net.Http.Json.JsonContent.Create(new { jsonrpc = "2.0", method = "notifications/initialized" });
            using var readyResponse = await http.SendAsync(ready);
            readyResponse.EnsureSuccessStatusCode();
            using var terminate = new HttpRequestMessage(HttpMethod.Delete, gateway.UrlFor("contract"));
            terminate.Headers.Authorization = new("Bearer", token);
            terminate.Headers.Add("Mcp-Session-Id", sessionId);
            terminate.Headers.Add("MCP-Protocol-Version", "2025-11-25");
            using var terminated = await http.SendAsync(terminate);
            Assert.True(terminated.IsSuccessStatusCode, $"DELETE returned {terminated.StatusCode}: {await terminated.Content.ReadAsStringAsync()}");

            // Category A: equal upstream names must retain both routing identity and their own permission class.
            await using var other = await InProcessMcpHttpServer.StartAsync<GatewayCollisionTool>();
            var second = config with { Name = "other", Url = other.Url };
            registry.Add(second);
            mounts.Grant("contract", ["depot", "other"]);
            await using var local = provider.CreateGatewaySession("contract", token, mounts.Set("contract")?.Context);
            await local.AddServerAsync(config);
            await local.AddServerAsync(second);
            Assert.Equal(["depot__contract", "other__contract"], local.Tools.Select(tool => tool.Function.Name));
            Assert.Equal(ToolPermissionClass.ReadOnly, local.ToolClasses["mcp__depot__contract"]);
            Assert.Equal(ToolPermissionClass.Destructive, local.ToolClasses["mcp__other__contract"]);
            var routed = await local.Tools.Single(tool => tool.Function.Name == "other__contract").Function.InvokeAsync(new AIFunctionArguments());
            Assert.Contains("other-upstream", JsonSerializer.Serialize(routed));

            // Category C: revoke during a call must close its client response as well as cancel the upstream.
            await client.ListToolsAsync();
            using var callDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var pending = client.CallToolAsync("other__contract", new Dictionary<string, object?> { ["waitForRevoke"] = true },
                cancellationToken: callDeadline.Token).AsTask();
            await GatewayCollisionTool.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            keyring.Revoke("contract", token);
            await Assert.ThrowsAnyAsync<ModelContextProtocol.McpException>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(2)));
            await GatewayCollisionTool.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
