using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Infrastructure.Tests.Mcp;

/// <summary>
/// AC-1288: the node port is fixed (AC-1284), so a second cockpit on the same machine finds it held. Before this,
/// Kestrel refused the whole endpoint on that — its loopback listener with it — and
/// <see cref="CockpitMcpEndpointHost.StartAsync"/> turned the failure into a log line the operator never sees, so
/// the Nodes tab showed an empty address field and no reason for it. Measured through the production mount path
/// with real sockets: nothing about this is visible from the settings or the view model.
/// </summary>
public sealed class NodeListenerPortInUseTests
{
    // Fixed rather than OS-assigned, because a held port is the thing under test. Below every platform's
    // ephemeral range, as `NodeEndpointSettings.DefaultPort` is, so no outbound socket can be handed it.
    private const int PairingPort = 21288;

    private const int McpPort = PairingPort + NodeEndpointSettings.McpPortOffset;

    /// <summary>
    /// Positive and negative control in one run: the same host, the same settings, the same port — the only
    /// difference is who holds it. Without the fix the second half loses the endpoint altogether and reports
    /// nothing; the first half is what says the rig can bind at all.
    /// </summary>
    [SkippableFact]
    public async Task WhenTheNodePortIsHeld_TheEndpointKeepsItsLoopbackListener_AndSaysWhyTheNetworkOneIsMissing()
    {
        Skip.If(NodeReachableAddress.Resolve() is null, "No real LAN interface on this machine — the positive control cannot bind a node address.");

        await using (var host = _Host())
        {
            await host.StartAsync(CancellationToken.None);

            Assert.NotEmpty(host.GetNodeAddresses());
            Assert.Null(host.NodeListenerError);
            await host.StopAsync(CancellationToken.None);
        }

        var occupier = new TcpListener(IPAddress.Any, McpPort);
        occupier.Start();
        try
        {
            await using var host = _Host();
            await host.StartAsync(CancellationToken.None);

            // The loopback listener is the half that has nothing to do with the network, and losing it took
            // cockpit-node's local MCP down with the bind that failed.
            Assert.Contains(host.GetServers(), server => server.Name == "cockpit-node-test" && server.Url is { Length: > 0 });
            Assert.Empty(host.GetNodeAddresses());
            Assert.Contains($"port {McpPort} is already in use", host.NodeListenerError ?? "", StringComparison.Ordinal);
            await host.StopAsync(CancellationToken.None);
        }
        finally
        {
            occupier.Stop();
        }
    }

    // The production constructor, and a NodeOnly endpoint registered the way DependencyInjection registers
    // cockpit-node — the flag is only reachable through the host's own startup path.
    private static CockpitMcpEndpointHost _Host()
    {
        var store = new NodeEndpointSettingsStore(Path.Combine(Path.GetTempPath(), $"ac-1288-{Guid.NewGuid():N}.json"));
        store.SaveAsync(new NodeEndpointSettings { Enabled = true, SharedSecret = "", Port = PairingPort }).GetAwaiter().GetResult();

        return new CockpitMcpEndpointHost(
            endpoints: [new CockpitMcpEndpoint("cockpit-node-test", typeof(NoTools), NodeOnly: true)],
            services: new ServiceCollection().BuildServiceProvider(),
            authKey: new McpAuthKey(),
            keyring: new SessionMcpKeyring(),
            nodeEndpointSettings: store,
            nodeCertificate: new NodeSelfSignedCertificate(Path.Combine(Path.GetTempPath(), $"ac-1288-{Guid.NewGuid():N}.pfx")),
            nodeSharedSecret: new NodeSharedSecret(),
            mounts: new SessionMcpMounts(),
            loggerFactory: NullLoggerFactory.Instance);
    }

    // No tools: the listeners are what this measures, and an endpoint with none still mounts both.
    internal sealed class NoTools;
}
