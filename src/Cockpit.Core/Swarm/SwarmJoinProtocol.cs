using Cockpit.Core.Mcp;

namespace Cockpit.Core.Swarm;

public enum SwarmHostKind { Desktop, Server }

public sealed record SwarmHost(SwarmHostKind Kind);
public sealed record SwarmJoinRequest(string Name, byte[] Certificate, string Address);
public sealed record SwarmJoinPrepare(NodePairingClaimRequest Claim, IReadOnlyList<SwarmRecord> Log);
public sealed record SwarmJoinCommit(NodePairingClaimRequest Claim, SwarmRecord Record, SwarmTransport Transport);
public sealed record SwarmTransport(string ConnectKey, string Pin, IReadOnlyList<NodeEndpointAddress> Endpoints);
public sealed record SwarmJoinHandshake(string Address, NodePairingOffer Offer, string Code, string Pin, byte[] PublicKey);
public sealed record SwarmJoinPending(string PairingId, string Name, string Code, DateTimeOffset ExpiresAt);
public sealed record SwarmMeshRequest(IReadOnlyList<SwarmRecord> Log, SwarmTransport Transport);

public static class SwarmJoinError
{
    public const string IsAServer = "is_a_server";
}
