using System.ComponentModel;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NSubstitute;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Infrastructure.Tests.Mcp;

/// <summary>
/// AC-1028: a tool call the AIFunctionFactory marshaller cannot bind — a missing required argument, or one that
/// will not deserialize — must come back as a readable tool error naming the bad parameter, not as an unhandled
/// exception logged as "notify threw an unhandled exception" with no indication of what the caller did wrong.
/// Exercised through a real mounted endpoint and a real MCP client, because the bug lived in the per-call filter
/// `MountAsync` wires up — nothing about it is visible from calling the tools class directly.
/// </summary>
public sealed class CockpitMcpEndpointHostArgumentErrorTests
{
    [Fact]
    public async Task MissingRequiredArgument_ReturnsAReadableToolError_NamingTheParameter()
    {
        await using var endpoint = await _MountedEndpoint.StartAsync();

        var result = await endpoint.Client.CallToolAsync("echo_pane", new Dictionary<string, object?>());

        Assert.True(result.IsError);
        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.Contains("toPaneId", text, StringComparison.Ordinal);
        Assert.DoesNotContain("StackTrace", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("at Cockpit.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidCall_StillSucceeds_TheFilterOnlyCatchesTheBrokenPath()
    {
        await using var endpoint = await _MountedEndpoint.StartAsync();

        var result = await endpoint.Client.CallToolAsync("echo_pane", new Dictionary<string, object?> { ["toPaneId"] = "pane-1" });

        Assert.NotEqual(true, result.IsError);
        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.Equal("pane-1", text);
    }

    internal sealed class EchoTools
    {
        [McpServerTool(Name = "echo_pane")]
        [Description("Echoes the given pane id back.")]
        public string Echo([Description("Required pane id.")] string toPaneId) => toPaneId;
    }

    private sealed class _MountedEndpoint(CockpitMcpEndpointHost host, McpClient client) : IAsyncDisposable
    {
        public McpClient Client { get; } = client;

        public static async Task<_MountedEndpoint> StartAsync()
        {
            var authKey = new McpAuthKey();
            var nodeSettings = Substitute.For<INodeEndpointSettingsStore>();
            nodeSettings.LoadAsync(Arg.Any<CancellationToken>()).Returns(NodeEndpointSettings.Default);

            var host = new CockpitMcpEndpointHost(
                endpoints: [],
                services: new ServiceCollection().BuildServiceProvider(),
                authKey: authKey,
                keyring: new SessionMcpKeyring(),
                nodeEndpointSettings: nodeSettings,
                nodeCertificate: new NodeSelfSignedCertificate(Path.Combine(Path.GetTempPath(), $"ac-1028-{Guid.NewGuid():N}.pfx")),
                nodeSharedSecret: new NodeSharedSecret(),
                loggerFactory: NullLoggerFactory.Instance);

            await host.MountAsync("ac-1028-echo", new EchoTools());
            var url = host.GetServers().Single(server => server.Name == "ac-1028-echo").Url;

            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Name = "ac-1028-echo",
                Endpoint = new Uri(url!),
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = new AuthenticationHeaderValue("Bearer", authKey.Value).ToString() },
            });
            var client = await McpClient.CreateAsync(transport);

            return new _MountedEndpoint(host, client);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await host.StopAsync(CancellationToken.None);
            await host.DisposeAsync();
        }
    }
}
