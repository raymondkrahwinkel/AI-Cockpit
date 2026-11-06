using Cockpit.Infrastructure.Mcp;
using ModelContextProtocol.Client;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// <see cref="McpClientConnector.ConnectAsync"/> against real in-process MCP HTTP servers (AC-928).
/// A server that answers the discover probe with an empty result still connects; a conforming one is untouched.
/// </summary>
public partial class McpClientConnectorTests
{
    // The revision the retry pins to — the newest one that still speaks the initialize handshake.
    private const string InitializeHandshakeVersion = "2025-11-25";

    [Fact]
    public async Task ConnectAsync_PreservesToolContractsAcrossDiscoverFallbackAndGateway()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var server = await InProcessMcpHttpServer.StartAsync<GatewayContractTool>(nonConformingDiscover: true);

        await using var client = await McpClientConnector.ConnectAsync(_TransportTo(server), null, timeout.Token);
        var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);

        // Connected through the retry — on the initialize handshake, with the server's tools actually in hand.
        Assert.Equal(InitializeHandshakeVersion, client.NegotiatedProtocolVersion);
        Assert.Equal("contract", Assert.Single(tools).Name);

        await _AssertGatewayContractAsync(server, client);
    }

    private static HttpClientTransport _TransportTo(InProcessMcpHttpServer server) =>
        new(new HttpClientTransportOptions { Endpoint = new Uri(server.Url) });
}
