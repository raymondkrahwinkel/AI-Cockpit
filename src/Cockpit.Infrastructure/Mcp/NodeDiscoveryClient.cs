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
    private readonly IPAddress? _localMulticastInterface;

    public NodeDiscoveryClient()
        : this(localMulticastInterface: null)
    {
    }

    // Test seam: send out one specific local interface instead of letting the OS pick, so a same-host test can
    // force the query — and the reply it provokes — over loopback regardless of the machine's real interfaces.
    internal NodeDiscoveryClient(IPAddress? localMulticastInterface)
    {
        _localMulticastInterface = localMulticastInterface;
    }

    public async Task<IReadOnlyList<NodeDiscoveryFound>> FindAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var found = new Dictionary<string, NodeDiscoveryFound>(StringComparer.Ordinal);

        using var client = new UdpClient(0);
        client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);

        if (_localMulticastInterface is { } localInterface)
        {
            client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, localInterface.GetAddressBytes());
        }

        var group = new IPEndPoint(IPAddress.Parse(NodeDiscoveryProtocol.MulticastGroup), NodeDiscoveryProtocol.Port);
        await client.SendAsync(NodeDiscoveryProtocol.QueryMarker, group, cancellationToken).ConfigureAwait(false);

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

                // Keyed by discovery id, not by address: a node with several NICs on this segment could answer
                // from more than one of them, and that is one node found, not two rows in the list.
                var address = $"{received.RemoteEndPoint.Address}:{announce.PairingPort}";
                found[announce.DiscoveryId] = new NodeDiscoveryFound(address, announce.DiscoveryId);
            }
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The listening window closed — this is how `FindAsync` is meant to end, not a failure to report.
        }

        return [.. found.Values];
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
