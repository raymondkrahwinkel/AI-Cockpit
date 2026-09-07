using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Infrastructure.Tests.Mcp;

/// <summary>
/// AC-1284: the path a real cockpit runs, which no test touched before. <see cref="NodeDiscoveryTests"/> forces
/// both ends onto <see cref="IPAddress.Loopback"/> through the test seams, and that is exactly why an ephemeral
/// pairing port and a single-interface multicast join stayed green here for as long as they did. These two use
/// the production constructors instead — no interface seam, no port seam — and cover the acceptance: a node
/// survives its own restart at the address a controller froze in its MCP registry.
/// </summary>
public sealed class NodeProductionPathTests : IAsyncLifetime
{
    private readonly string _configPath = Path.Combine(Path.GetTempPath(), $"node-production-{Guid.NewGuid():N}.json");
    private readonly string _certificatePath = Path.Combine(Path.GetTempPath(), $"node-production-{Guid.NewGuid():N}.pfx");
    private readonly string _discoveryIdPath = Path.Combine(Path.GetTempPath(), $"node-production-id-{Guid.NewGuid():N}.txt");

    // A fixed port, which is the production shape and the thing under test — but not the production number,
    // which a cockpit running as a node on this machine already holds. Deliberately below the OS's ephemeral
    // range, so no other test's port-0 bind can be handed it while this one is between its two starts.
    private const int PairingPort = 21284;

    private NodeEndpointSettingsStore _store = null!;
    private NodeSelfSignedCertificate _certificate = null!;

    public async Task InitializeAsync()
    {
        _store = new NodeEndpointSettingsStore(_configPath);
        await _store.SaveAsync(new NodeEndpointSettings { Enabled = true, SharedSecret = "", Port = PairingPort });
        _certificate = new NodeSelfSignedCertificate(_certificatePath);
    }

    /// <summary>
    /// The acceptance of AC-1284. A controller stores the node's address once, at pairing time, and nothing in
    /// this codebase ever told it a new one — so the node's port surviving its own restart is what keeps the
    /// coupling alive. Both halves are asserted: the same port comes back, and the address the first run handed
    /// out still reaches the second one. With the pre-fix ephemeral bind both fail, the second by connection
    /// refusal, which is precisely what "it worked once and never again" looked like.
    /// </summary>
    [Fact]
    public async Task ARestartOfTheNode_KeepsTheAddressAControllerAlreadyStored()
    {
        var before = await _StartPairingHostAsync();
        Assert.NotNull(before.BoundPort);
        var storedAddress = $"127.0.0.1:{before.BoundPort}";

        // Control first: the address is reachable while the run that handed it out is still up, so a failure
        // after the restart is the restart and not a rig that never worked.
        Assert.Equal(NodePairingCode.Digits, (await new NodePairingClient().BeginAsync(storedAddress, "the controller")).Code.Length);
        await before.DisposeAsync();

        var after = await _StartPairingHostAsync();
        try
        {
            // The coupling first, the port second: the coupling is what breaks for the operator, and asserting it
            // first is what makes the counterproof read as a connection refusal rather than as two numbers.
            var handshake = await new NodePairingClient().BeginAsync(storedAddress, "the controller");
            Assert.Equal(NodePairingCode.Digits, handshake.Code.Length);
            Assert.Equal(before.BoundPort, after.BoundPort);
        }
        finally
        {
            await after.DisposeAsync();
        }
    }

    /// <summary>
    /// Discovery with the constructors production uses: <see cref="NodeVisibilityPolicy"/> over this machine's
    /// real interfaces, a responder that joins the group on all of them, and a finder that queries all of them.
    /// Asserted on this node's own discovery id in both directions rather than on a count, so another cockpit
    /// answering on the segment neither passes this test nor fails it.
    /// </summary>
    [SkippableFact]
    public async Task TheProductionDiscoveryPath_FindsThisNode_AndDoesNotAfterItStops()
    {
        Skip.If(NodeReachableAddress.Resolve() is null, "No real LAN interface on this machine — nothing for the production discovery path to run over.");

        var pairingHost = await _StartPairingHostAsync();
        // The responder stays silent for a node whose pairing listener never came up, so a bind failure would
        // otherwise read here as "discovery found nothing" — a different finding entirely.
        Assert.NotNull(pairingHost.BoundPort);
        var discoveryId = new NodeDiscoveryId(_discoveryIdPath);
        var responder = new NodeDiscoveryResponder(_store, new NodeVisibilityPolicy(_store), discoveryId, pairingHost, NullLoggerFactory.Instance);
        await responder.StartAsync(CancellationToken.None);

        try
        {
            var finder = new NodeDiscoveryClient();
            var found = await finder.FindAsync(TimeSpan.FromSeconds(3));

            Assert.Contains(found, node => node.DiscoveryId == discoveryId.Value);

            // The negative control, in the same window and through the same finder: with the responder gone this
            // node is absent, so the hit above is this node answering and not a neighbour's cockpit.
            await responder.DisposeAsync();
            var afterStopping = await finder.FindAsync(TimeSpan.FromSeconds(1));

            Assert.DoesNotContain(afterStopping, node => node.DiscoveryId == discoveryId.Value);
        }
        finally
        {
            await responder.DisposeAsync();
            await pairingHost.DisposeAsync();
        }
    }

    // A fresh broker each time, which is what a restart gives: the pending pairings it holds live in memory only.
    private async Task<NodePairingHost> _StartPairingHostAsync()
    {
        var broker = new NodePairingBroker(_store, _certificate, new NodeSharedSecret(), []);
        var host = new NodePairingHost(_store, broker, _certificate, new NodeVisibilityPolicy(_store), NullLoggerFactory.Instance);
        await host.StartAsync(CancellationToken.None);
        return host;
    }

    public Task DisposeAsync()
    {
        _certificate.Dispose();

        foreach (var path in new[] { _configPath, _certificatePath, _discoveryIdPath })
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }
}
