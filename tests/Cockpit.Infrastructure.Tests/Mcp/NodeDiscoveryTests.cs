using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Infrastructure.Tests.Mcp;

/// <summary>
/// Discovery end to end (AC-793): a real UDP responder and a real UDP finder, both forced onto the loopback
/// interface so the test is deterministic regardless of what network interfaces the machine running it has, and
/// a real <see cref="NodePairingHost"/> so criterion 1a can prove a found address reaches the exact same
/// handshake a typed one does — <see cref="NodePairingHandshakeTests"/> for the manual side of that comparison.
/// </summary>
/// <remarks>
/// Two machines would be better and were not available; this runs the finder and the node in one process over
/// loopback, the same limitation <see cref="NodePairingHandshakeTests"/> notes for the manual route.
/// </remarks>
public class NodeDiscoveryTests : IAsyncLifetime
{
    private readonly string _configPath = Path.Combine(Path.GetTempPath(), $"node-discovery-{Guid.NewGuid():N}.json");
    private readonly string _certificatePath = Path.Combine(Path.GetTempPath(), $"node-discovery-{Guid.NewGuid():N}.pfx");
    private readonly string _discoveryIdPath = Path.Combine(Path.GetTempPath(), $"node-discovery-id-{Guid.NewGuid():N}.txt");

    private NodeEndpointSettingsStore _store = null!;
    private NodeSelfSignedCertificate _certificate = null!;
    private NodePairingBroker _broker = null!;

    public async Task InitializeAsync()
    {
        _store = new NodeEndpointSettingsStore(_configPath);
        await _store.SaveAsync(new NodeEndpointSettings { Enabled = true, SharedSecret = "" });
        _certificate = new NodeSelfSignedCertificate(_certificatePath);
        _broker = new NodePairingBroker(_store, _certificate, new NodeSharedSecret(), []);
    }

    private async Task<NodePairingHost> _StartPairingHostAsync(Func<IEnumerable<IPNetwork>> ownRanges)
    {
        var visibility = new NodeVisibilityPolicy(_store, ownRanges);
        var host = new NodePairingHost(_store, _broker, _certificate, visibility, NullLoggerFactory.Instance);
        await host.StartAsync(CancellationToken.None);
        return host;
    }

    private async Task<NodeDiscoveryResponder> _StartResponderAsync(NodePairingHost pairingHost, Func<IEnumerable<IPNetwork>> ownRanges)
    {
        var visibility = new NodeVisibilityPolicy(_store, ownRanges);
        // Port 0: the OS picks one nobody else already holds, read back below via BoundPort — the same
        // collision-free idiom NodePairingHost.BoundPort already uses, rather than this test guessing a number.
        var responder = new NodeDiscoveryResponder(
            _store, visibility, new NodeDiscoveryId(_discoveryIdPath), pairingHost, NullLoggerFactory.Instance, IPAddress.Loopback, port: 0);
        await responder.StartAsync(CancellationToken.None);
        return responder;
    }

    // Nothing is bound here (used only where no responder started, or a responder's own port must be avoided) —
    // reserving and releasing an OS-assigned port is the same free-of-collisions idiom as `_StartResponderAsync`.
    private static int _ReserveEphemeralPort()
    {
        using var socket = new UdpClient(0);
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }

    [Fact]
    public async Task ANodeOnTheOwnRange_IsFoundAndPairableThroughTheSameHandshakeAsTheManualRoute()
    {
        var pairingHost = await _StartPairingHostAsync(() => [IPNetwork.Parse("127.0.0.0/8")]);
        var responder = await _StartResponderAsync(pairingHost, () => [IPNetwork.Parse("127.0.0.0/8")]);
        try
        {
            var finder = new NodeDiscoveryClient(IPAddress.Loopback, responder.BoundPort!.Value);
            var results = await finder.FindAsync(TimeSpan.FromSeconds(3));

            var foundNode = Assert.Single(results);
            Assert.Equal($"127.0.0.1:{pairingHost.BoundPort}", foundNode.Address);

            // Criterion 1a: the address discovery found reaches the exact same `BeginAsync` the Security tab's
            // typed-address field calls — no second pairing code path for a discovered address.
            var client = new NodePairingClient();
            var handshake = await client.BeginAsync(foundNode.Address, "the controller");
            Assert.Equal(NodePairingCode.Digits, handshake.Code.Length);
        }
        finally
        {
            await responder.DisposeAsync();
            await pairingHost.DisposeAsync();
        }
    }

    /// <summary>
    /// AC-1075: before the port became a test seam, client and responder always used the one hardcoded
    /// <see cref="NodeDiscoveryProtocol.Port"/> — the same value any other same-host process also binds. Pins
    /// the fix at the seam: a query scoped elsewhere hears nothing, one scoped to the real port still finds it.
    /// Never queries <see cref="NodeDiscoveryProtocol.Port"/> itself as "elsewhere" — a live Cockpit.App with
    /// node discovery on binds exactly that one, and this test must not depend on it staying silent.
    /// </summary>
    [Fact]
    public async Task AQueryScopedToADifferentPort_DoesNotFindThisNode_ButItsOwnPortStillDoes()
    {
        var pairingHost = await _StartPairingHostAsync(() => [IPNetwork.Parse("127.0.0.0/8")]);
        var responder = await _StartResponderAsync(pairingHost, () => [IPNetwork.Parse("127.0.0.0/8")]);
        try
        {
            var wrongPortFinder = new NodeDiscoveryClient(IPAddress.Loopback, _ReserveEphemeralPort());
            Assert.Empty(await wrongPortFinder.FindAsync(TimeSpan.FromMilliseconds(500)));

            var rightPortFinder = new NodeDiscoveryClient(IPAddress.Loopback, responder.BoundPort!.Value);
            Assert.Single(await rightPortFinder.FindAsync(TimeSpan.FromSeconds(3)));
        }
        finally
        {
            await responder.DisposeAsync();
            await pairingHost.DisposeAsync();
        }
    }

    [Fact]
    public async Task ANodeOutsideTheOwnRange_WithAnEmptyWhitelist_AnswersNothing()
    {
        // "Own range" deliberately excludes loopback, so the finder — which necessarily calls in over loopback in
        // a same-host test — reads as an outside caller. Criterion 2, the failure direction.
        var pairingHost = await _StartPairingHostAsync(() => []);
        var responder = await _StartResponderAsync(pairingHost, () => []);
        try
        {
            var finder = new NodeDiscoveryClient(IPAddress.Loopback, responder.BoundPort!.Value);
            var results = await finder.FindAsync(TimeSpan.FromMilliseconds(500));

            Assert.Empty(results);
        }
        finally
        {
            await responder.DisposeAsync();
            await pairingHost.DisposeAsync();
        }
    }

    [Fact]
    public async Task Discovery_AnswersAWhitelistedCaller_OutsideItsOwnRange()
    {
        // Criterion 3, first moment: the whitelist gates a discovery reply, independently of the pairing gate
        // below.
        await _store.SaveAsync((await _store.LoadAsync()) with { AllowedDiscoveryRanges = ["127.0.0.0/8"] });
        var pairingHost = await _StartPairingHostAsync(() => []);
        var responder = await _StartResponderAsync(pairingHost, () => []);
        try
        {
            var finder = new NodeDiscoveryClient(IPAddress.Loopback, responder.BoundPort!.Value);
            var results = await finder.FindAsync(TimeSpan.FromSeconds(3));

            Assert.Single(results);
        }
        finally
        {
            await responder.DisposeAsync();
            await pairingHost.DisposeAsync();
        }
    }

    [Fact]
    public async Task PairingRequest_FromOutsideTheOwnRange_WithAnEmptyWhitelist_IsRefusedBeforeTheBrokerSeesIt()
    {
        // Criterion 3, second moment: the same "outside own range, empty whitelist" posture, checked at pairing
        // acceptance rather than at a discovery reply. A caller that skips discovery and guesses the address is
        // refused here just the same — "hij ziet me toch niet" would otherwise only be true for one of the two
        // entrances.
        var pairingHost = await _StartPairingHostAsync(() => []);
        try
        {
            var client = new NodePairingClient();
            var refusal = await Assert.ThrowsAsync<NodePairingException>(
                () => client.BeginAsync($"127.0.0.1:{pairingHost.BoundPort}", "a stranger"));

            Assert.Equal(NodePairingError.NotVisible, refusal.Error);
        }
        finally
        {
            await pairingHost.DisposeAsync();
        }
    }

    [Fact]
    public async Task PairingRequest_FromOutsideTheOwnRange_ButWhitelisted_Succeeds()
    {
        await _store.SaveAsync((await _store.LoadAsync()) with { AllowedDiscoveryRanges = ["127.0.0.0/8"] });
        var pairingHost = await _StartPairingHostAsync(() => []);
        try
        {
            var client = new NodePairingClient();
            var handshake = await client.BeginAsync($"127.0.0.1:{pairingHost.BoundPort}", "the controller");

            Assert.Equal(NodePairingCode.Digits, handshake.Code.Length);
        }
        finally
        {
            await pairingHost.DisposeAsync();
        }
    }

    [Fact]
    public async Task Start_WhileTheNodeSwitchIsOff_AnswersNothing()
    {
        var offPath = Path.Combine(Path.GetTempPath(), $"node-discovery-off-{Guid.NewGuid():N}.json");
        var offStore = new NodeEndpointSettingsStore(offPath);
        var visibility = new NodeVisibilityPolicy(offStore, () => [IPNetwork.Parse("127.0.0.0/8")]);
        var pairingHost = new NodePairingHost(offStore, _broker, _certificate, visibility, NullLoggerFactory.Instance);
        var responder = new NodeDiscoveryResponder(
            offStore, visibility, new NodeDiscoveryId(_discoveryIdPath), pairingHost, NullLoggerFactory.Instance, IPAddress.Loopback, port: 0);

        await pairingHost.StartAsync(CancellationToken.None);
        await responder.StartAsync(CancellationToken.None);

        try
        {
            // The switch is off, so StartAsync returned before binding — BoundPort is null. Any port nobody
            // holds proves the same "answers nothing" point; there is no responder port to scope this to.
            var finder = new NodeDiscoveryClient(IPAddress.Loopback, _ReserveEphemeralPort());
            var results = await finder.FindAsync(TimeSpan.FromMilliseconds(500));

            Assert.Empty(results);
        }
        finally
        {
            await responder.DisposeAsync();
            await pairingHost.DisposeAsync();
            File.Delete(offPath);
        }
    }

    [Fact]
    public async Task TheAnnouncePayload_CarriesOnlyTheMarkerIdAndPort_NoMachineNameOrOtherField()
    {
        // Criterion 4, checked on the wire rather than only against the record's shape — a future field added to
        // `NodeDiscoveryAnnounce` for an unrelated reason would still be caught here.
        var pairingHost = await _StartPairingHostAsync(() => [IPNetwork.Parse("127.0.0.0/8")]);
        var responder = await _StartResponderAsync(pairingHost, () => [IPNetwork.Parse("127.0.0.0/8")]);
        try
        {
            using var raw = new UdpClient(0);
            raw.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, IPAddress.Loopback.GetAddressBytes());
            var group = new IPEndPoint(IPAddress.Parse(NodeDiscoveryProtocol.MulticastGroup), responder.BoundPort!.Value);
            await raw.SendAsync(NodeDiscoveryProtocol.QueryMarker, group);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var reply = await raw.ReceiveAsync(timeout.Token);

            var json = Encoding.UTF8.GetString(reply.Buffer);
            using var document = JsonDocument.Parse(json);
            var propertyNames = document.RootElement.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal);

            Assert.Equal(
                new[] { "discoveryId", "marker", "pairingPort" }.OrderBy(name => name, StringComparer.Ordinal),
                propertyNames);
            Assert.DoesNotContain(Environment.MachineName, json, StringComparison.Ordinal);
        }
        finally
        {
            await responder.DisposeAsync();
            await pairingHost.DisposeAsync();
        }
    }

    public Task DisposeAsync()
    {
        _certificate.Dispose();

        foreach (var path in new[] { _configPath, _certificatePath, _discoveryIdPath })
        {
            var directory = Path.GetDirectoryName(path)!;
            var fileName = Path.GetFileName(path);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(directory, fileName + "*"))
            {
                File.Delete(file);
            }
        }

        return Task.CompletedTask;
    }
}
