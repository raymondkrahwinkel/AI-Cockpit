using System.Text.Json;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NSubstitute;

namespace Cockpit.Core.Tests.Mcp;

public class SessionMcpGatewayContractTests
{
    // Category D: the wire contract must retain image blocks and tool metadata through the gateway.
    [Fact]
    public async Task Gateway_PreservesUpstreamToolAndResultContracts()
    {
        await using var upstream = await InProcessMcpHttpServer.StartAsync<GatewayContractTool>();
        var config = new McpServerConfig { Name = "depot", Transport = McpTransport.Http, Url = upstream.Url };
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersForProjectAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns([config]);
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
            await File.WriteAllTextAsync(path, """{"SessionMcpGatewayEnabled":true}""");
            await using var gateway = new SessionMcpGatewayHost(auth, keyring, NullLoggerFactory.Instance, path, provider, mounts);
            await gateway.StartAsync(CancellationToken.None);
            await using var direct = await McpClient.CreateAsync(new HttpClientTransport(new() { Endpoint = new Uri(upstream.Url) }));
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
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

internal sealed class GatewayContractTool
{
    [McpServerTool(Name = "contract", ReadOnly = true, Destructive = false)]
    public CallToolResult Contract() => new()
    {
        Content = [new TextContentBlock { Text = "first" }, new ImageContentBlock { Data = "aGVsbG8="u8.ToArray(), MimeType = "image/png" }, new TextContentBlock { Text = "last" }],
    };
}
