using System.Text.Json;

namespace Cockpit.Core.Mcp;

// The wire shape of a discovery round-trip (AC-793): a query into a multicast group and, for whoever
// `INodeVisibilityPolicy` lets answer, a unicast reply naming nothing but what a stranger on the segment is
// owed — see `NodeDiscoveryAnnounce` for why a machine name is not in it. This is the second entrance to the
// same handshake `NodePairingProtocol` describes; nothing here grants anything, it only helps a caller find an
// address to hand `INodePairingClient.BeginAsync`.
public static class NodeDiscoveryProtocol
{
    // Organization-local multicast scope (RFC 2365, 239.255.0.0/16) — routers do not forward it past the local
    // network, which is exactly the boundary discovery's default posture wants and does not have to reimplement
    // with its own TTL bookkeeping.
    public const string MulticastGroup = "239.255.83.121";

    public const int Port = 52381;

    // Sent by a finder into the multicast group. Content-free on purpose: replying is the interesting part, and a
    // query carrying a field would need that field validated against whatever a stranger on the segment supplied.
    public static readonly byte[] QueryMarker = "cockpit-node-discover-v1"u8.ToArray();
}

// What a node unicasts back to a query it chose to answer. `DiscoveryId` is stable across announces so a finder
// listening for longer than one round does not show the same node twice; `PairingPort` is the only thing a
// finder needs to hand `INodePairingClient.BeginAsync` the same address a typed-in one would be. Deliberately
// missing: `Environment.MachineName`, a username, a version — a broadcast's audience is everything on the
// segment, not the one caller a pairing response goes to, and that difference in audience is a difference in
// payload (AC-793 uitzoekpunt 2). The node's display name still arrives, just later — over TLS, inside
// `NodePairingOffer`, only once a pairing has actually started.
public sealed record NodeDiscoveryAnnounce(string DiscoveryId, int PairingPort)
{
    public const string CurrentMarker = "cockpit-node-announce-v1";

    public string Marker { get; init; } = CurrentMarker;
}

// A node found on the network, from the finder's side. `Address` is the caller-visible `host:port` string —
// built from where the reply actually came from, not from anything the payload claimed — ready to hand straight
// to `INodePairingClient.BeginAsync`, unchanged, the same as an address typed by hand.
public sealed record NodeDiscoveryFound(string Address, string DiscoveryId);

public static class NodeDiscoveryJson
{
    // Same shape as `NodePairingJson.Options` and for the same reason: camelCase to match every other JSON
    // surface here, case-insensitive reading so the two ends never disagree over capitalisation.
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
}
