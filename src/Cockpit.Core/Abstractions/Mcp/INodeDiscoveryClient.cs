using Cockpit.Core.Mcp;

namespace Cockpit.Core.Abstractions.Mcp;

/// <summary>
/// The finder half of discovery (AC-793): asks the local network's multicast group who is out there, and
/// collects whatever nodes choose to answer. What comes back is only ever an address a caller could also have
/// typed by hand — it feeds <see cref="INodePairingClient.BeginAsync"/>, nothing more.
/// </summary>
public interface INodeDiscoveryClient
{
    /// <summary>
    /// Sends one query and listens for <paramref name="timeout"/>, returning every distinct node that answered
    /// (deduplicated by <see cref="NodeDiscoveryFound.DiscoveryId"/>). An empty list means nothing answered —
    /// out of range, the switch is off everywhere on the segment, or the network drops multicast — not an error.
    /// </summary>
    Task<IReadOnlyList<NodeDiscoveryFound>> FindAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}
