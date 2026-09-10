using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;

namespace Cockpit.Infrastructure.Mcp;

// The finder half of discovery (AC-793): one query into the multicast group, then a fixed listening window for
// whatever nodes choose to answer. Ordinary `UdpClient`, no listener of its own kept between calls — the
// Security tab's "discover" action is something the operator presses, not a background subscription.
internal sealed class NodeDiscoveryClient : INodeDiscoveryClient, ISingletonService
{
    private readonly NodeDiscoveryId _discoveryId;
    private readonly IPAddress? _localMulticastInterface;
    private readonly int _port;

    public NodeDiscoveryClient()
        : this(new NodeDiscoveryId(), localMulticastInterface: null)
    {
    }

    // Test seam: send out one specific local interface instead of letting the OS pick, forcing the query over
    // loopback. `port` (AC-1075) is a second seam: the production port is one shared value any other same-host
    // process also binds, so a test needs its own to avoid cross-talk with one of those.
    internal NodeDiscoveryClient(NodeDiscoveryId discoveryId, IPAddress? localMulticastInterface, int? port = null)
    {
        _discoveryId = discoveryId;
        _localMulticastInterface = localMulticastInterface;
        _port = port ?? NodeDiscoveryProtocol.Port;
    }

    public async Task<IReadOnlyList<NodeDiscoveryFound>> FindAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var found = new Dictionary<string, NodeDiscoveryFound>(StringComparer.Ordinal);

        using var client = new UdpClient(0);
        client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);

        var group = new IPEndPoint(IPAddress.Parse(NodeDiscoveryProtocol.MulticastGroup), _port);

        if (_localMulticastInterface is { } localInterface)
        {
            client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, localInterface.GetAddressBytes());
            await client.SendAsync(NodeDiscoveryProtocol.QueryMarker, group, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _QueryEveryRealInterfaceAsync(client, group, cancellationToken).ConfigureAwait(false);
        }

        using var timeoutCancellation = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancellation.Token);

        try
        {
            while (true)
            {
                UdpReceiveResult received;
                try
                {
                    received = await client.ReceiveAsync(linked.Token).ConfigureAwait(false);
                }
                catch (SocketException)
                {
                    // A stale listener on the segment answering with an ICMP port-unreachable turns into a
                    // connection-reset error on the next receive for a connectionless socket. One bad neighbour
                    // must not cost every node that already answered inside this window.
                    continue;
                }

                if (_TryParse(received.Buffer) is not { } announce)
                {
                    // Not one of ours (wrong marker, or noise on the group) — keep listening for the rest of the
                    // window rather than treating an unrelated packet as the end of discovery.
                    continue;
                }

                if (announce.DiscoveryId == _discoveryId.Value)
                {
                    continue;
                }

                // Keyed by discovery id, not by address: a node with several NICs on this segment could answer
                // from more than one of them, and that is one node found, not two rows in the list.
                var address = $"{received.RemoteEndPoint.Address}:{announce.PairingPort}";
                // An address substitutes for a missing id only as this round's deduplication key.
                var discoveryId = announce.DiscoveryId ?? address;
                found[discoveryId] = new NodeDiscoveryFound(address, discoveryId);
            }
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The listening window closed — this is how `FindAsync` is meant to end, not a failure to report.
        }

        return [.. found.Values];
    }

    // AC-1284: the query's half of the same fix `NodeDiscoveryResponder._JoinEveryRealInterface` makes — one send
    // per real interface instead of letting the routing table choose one, so a node on the Wi-Fi is found by a
    // controller whose default route is the cable. Replies come back unicast, so one receiving socket still does.
    private static async Task _QueryEveryRealInterfaceAsync(UdpClient client, IPEndPoint group, CancellationToken cancellationToken)
    {
        var sent = 0;
        foreach (var candidate in NodeReachableAddress.RealUnicastAddresses())
        {
            try
            {
                client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, candidate.Address.Address.GetAddressBytes());
                await client.SendAsync(NodeDiscoveryProtocol.QueryMarker, group, cancellationToken).ConfigureAwait(false);
                sent++;
            }
            catch (SocketException)
            {
                // Same posture as the responder's join: one unusable NIC costs its own query, not the others'.
            }
        }

        if (sent == 0)
        {
            // No real LAN interface, or none of them took the query — the kernel's own choice is all that is left,
            // and on a loopback-only machine it is also the right one.
            client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, IPAddress.Any.GetAddressBytes());
            await client.SendAsync(NodeDiscoveryProtocol.QueryMarker, group, cancellationToken).ConfigureAwait(false);
        }
    }

    private static NodeDiscoveryAnnounce? _TryParse(byte[] buffer)
    {
        try
        {
            var announce = JsonSerializer.Deserialize<NodeDiscoveryAnnounce>(buffer, NodeDiscoveryJson.Options);
            return announce is { Marker: NodeDiscoveryAnnounce.CurrentMarker } ? announce : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
