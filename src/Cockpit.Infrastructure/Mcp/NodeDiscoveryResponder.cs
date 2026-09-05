using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;

namespace Cockpit.Infrastructure.Mcp;

// AC-793: the node half of discovery — joins the multicast group, answers only for a caller NodeVisibilityPolicy
// allows, started only when the node master switch is on. ponytail: multicast only, no subnet-broadcast
// fallback — some Wi-Fi/guest VLANs drop it; upgrade path is a UdpClient subnet broadcast alongside multicast.
internal sealed class NodeDiscoveryResponder : IHostedService, ISingletonService, IAsyncDisposable
{
    private readonly INodeEndpointSettingsStore _settings;
    private readonly INodeVisibilityPolicy _visibility;
    private readonly NodeDiscoveryId _discoveryId;
    private readonly NodePairingHost _pairingHost;
    private readonly ILogger<NodeDiscoveryResponder> _logger;
    private readonly IPAddress? _localMulticastInterface;
    private readonly int _port;

    private UdpClient? _client;
    private CancellationTokenSource? _loopCancellation;
    private Task? _loop;

    // The port actually bound — equal to `_port` unless that was 0, in which case this is what the OS assigned.
    // Test seam (AC-1075): a test passes 0 and reads this back, the same OS-guaranteed-unique-port idiom as
    // `NodePairingHost.BoundPort`, rather than picking a port itself and hoping nothing else already holds it.
    internal int? BoundPort { get; private set; }

    public NodeDiscoveryResponder(
        INodeEndpointSettingsStore settings,
        INodeVisibilityPolicy visibility,
        NodeDiscoveryId discoveryId,
        NodePairingHost pairingHost,
        ILoggerFactory loggerFactory)
        : this(settings, visibility, discoveryId, pairingHost, loggerFactory, localMulticastInterface: null)
    {
    }

    // Test seam: join the multicast group on one specific local interface, forcing delivery over loopback.
    // `port` (AC-1075) is a second seam: the production port is one shared value any other same-host process
    // also binds, so a test needs its own to avoid cross-talk with one of those.
    internal NodeDiscoveryResponder(
        INodeEndpointSettingsStore settings,
        INodeVisibilityPolicy visibility,
        NodeDiscoveryId discoveryId,
        NodePairingHost pairingHost,
        ILoggerFactory loggerFactory,
        IPAddress? localMulticastInterface,
        int? port = null)
    {
        _settings = settings;
        _visibility = visibility;
        _discoveryId = discoveryId;
        _pairingHost = pairingHost;
        _logger = loggerFactory.CreateLogger<NodeDiscoveryResponder>();
        _localMulticastInterface = localMulticastInterface;
        _port = port ?? NodeDiscoveryProtocol.Port;
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
            client.Client.Bind(new IPEndPoint(IPAddress.Any, _port));
            BoundPort = ((IPEndPoint)client.Client.LocalEndPoint!).Port;

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
                // Best effort, deliberately broad — a corrupt cockpit.json throwing inside IsAllowedAsync must
                // not take down the listen loop for one bad query. OperationCanceledException is excluded so a
                // genuine shutdown ends the loop the same way ReceiveAsync does, not logged as a query gone wrong.
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
