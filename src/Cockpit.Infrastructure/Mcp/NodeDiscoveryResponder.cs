using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;

namespace Cockpit.Infrastructure.Mcp;

// The node half of discovery (AC-793): joins the multicast group `NodeDiscoveryProtocol` names and answers a
// query with exactly what `NodeDiscoveryAnnounce` allows — but only for a caller `NodeVisibilityPolicy` lets see
// this node at all. Started only when the node master switch is on, the same posture `NodePairingHost` takes:
// off, this binds nothing, so an unpaired cockpit nobody meant as a node has no discovery socket to find either.
//
// ponytail: multicast only, no subnet-broadcast fallback. The ticket flagged this as a real risk — some Wi-Fi
// access points and guest VLANs drop multicast between clients — and it was measured rather than assumed:
// loopback multicast delivery works in this dev sandbox (send → receive round-trip observed before writing this
// file). The manual address route from AC-792 is unaffected either way; a network where this silently finds
// nothing is a missing convenience, not a missing pairing path. Upgrade path if that turns out to matter on
// Raymond's actual network: a `UdpClient` broadcast to the subnet's broadcast address as a second send alongside
// the multicast one, same visibility check, same reply.
internal sealed class NodeDiscoveryResponder : IHostedService, ISingletonService, IAsyncDisposable
{
    private readonly INodeEndpointSettingsStore _settings;
    private readonly INodeVisibilityPolicy _visibility;
    private readonly NodeDiscoveryId _discoveryId;
    private readonly NodePairingHost _pairingHost;
    private readonly ILogger<NodeDiscoveryResponder> _logger;
    private readonly IPAddress? _localMulticastInterface;

    private UdpClient? _client;
    private CancellationTokenSource? _loopCancellation;
    private Task? _loop;

    public NodeDiscoveryResponder(
        INodeEndpointSettingsStore settings,
        INodeVisibilityPolicy visibility,
        NodeDiscoveryId discoveryId,
        NodePairingHost pairingHost,
        ILoggerFactory loggerFactory)
        : this(settings, visibility, discoveryId, pairingHost, loggerFactory, localMulticastInterface: null)
    {
    }

    // Test seam: join the multicast group on one specific local interface instead of every interface (the
    // production default), so a same-host test can force delivery over loopback regardless of what real network
    // interfaces the machine running the test happens to have.
    internal NodeDiscoveryResponder(
        INodeEndpointSettingsStore settings,
        INodeVisibilityPolicy visibility,
        NodeDiscoveryId discoveryId,
        NodePairingHost pairingHost,
        ILoggerFactory loggerFactory,
        IPAddress? localMulticastInterface)
    {
        _settings = settings;
        _visibility = visibility;
        _discoveryId = discoveryId;
        _pairingHost = pairingHost;
        _logger = loggerFactory.CreateLogger<NodeDiscoveryResponder>();
        _localMulticastInterface = localMulticastInterface;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!settings.Enabled)
        {
            return;
        }

        try
        {
            var client = new UdpClient();
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            client.Client.Bind(new IPEndPoint(IPAddress.Any, NodeDiscoveryProtocol.Port));

            var group = IPAddress.Parse(NodeDiscoveryProtocol.MulticastGroup);
            if (_localMulticastInterface is { } localInterface)
            {
                client.JoinMulticastGroup(group, localInterface);
            }
            else
            {
                client.JoinMulticastGroup(group);
            }

            _client = client;

            var loopCancellation = new CancellationTokenSource();
            _loopCancellation = loopCancellation;
            _loop = _ListenAsync(client, loopCancellation.Token);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // A cockpit that cannot open a multicast socket — the port is taken, or the platform refuses the
            // group join — is still a working cockpit. It just cannot be found on the network this run, which the
            // Security tab's "found nothing" is indistinguishable from anyway.
            _logger.LogWarning(ex, "Could not start node discovery.");
        }
    }

    private async Task _ListenAsync(UdpClient client, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await client.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException or SocketException)
            {
                return;
            }

            if (!received.Buffer.AsSpan().SequenceEqual(NodeDiscoveryProtocol.QueryMarker))
            {
                // Unauthenticated, on a network interface, multicast group: something other than a query can land
                // here (another app entirely, a stale client of a future protocol version) and is silently
                // ignored — the same posture `NodePairingHost._ReadAsync` takes for a malformed pairing body.
                continue;
            }

            try
            {
                await _RespondAsync(client, received.RemoteEndPoint, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Best effort, deliberately broad: `_RespondAsync` reaches into `_visibility.IsAllowedAsync`,
                // which reads `cockpit.json` — a corrupt config on disk throwing there must not take down the
                // whole listen loop for the rest of the process's life over one bad query. A shutdown mid-send
                // surfaces here too (`ObjectDisposedException`) and is equally harmless to log: `StopAsync`
                // already cancelled the loop, so the next `ReceiveAsync` above exits it. `OperationCanceledException`
                // is excluded so a genuine shutdown still ends the loop the same way the receive above does,
                // rather than being logged as if it were a query gone wrong.
                _logger.LogWarning(ex, "Could not answer a discovery query.");
            }
        }
    }

    private async Task _RespondAsync(UdpClient client, IPEndPoint sender, CancellationToken cancellationToken)
    {
        // Criterion 3, the discovery half: nothing past here runs for a caller outside this node's own range and
        // outside the whitelist. The other half of the same criterion is the check `NodePairingHost` makes on
        // `/pair/request` — a caller that never discovers this node still cannot reach it by guessing the address.
        if (!await _visibility.IsAllowedAsync(sender.Address, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (_pairingHost.BoundPort is not { } pairingPort)
        {
            // The pairing listener has not finished starting yet, or failed to. Answering "found" for a node with
            // no pairing port would be a find that leads nowhere — quieter to just not answer this query.
            return;
        }

        var announce = new NodeDiscoveryAnnounce(_discoveryId.Value, pairingPort);
        var payload = JsonSerializer.SerializeToUtf8Bytes(announce, NodeDiscoveryJson.Options);
        await client.SendAsync(payload, sender, cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _loopCancellation?.Cancel();
        _client?.Dispose();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
            }
        }
    }
}
