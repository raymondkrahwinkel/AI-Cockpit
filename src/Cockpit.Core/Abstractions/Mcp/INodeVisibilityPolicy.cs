using System.Net;

namespace Cockpit.Core.Abstractions.Mcp;

/// <summary>
/// Whether a caller address may see this node at all (AC-793). The same yes/no gates both discovery's reply and
/// a pairing request's acceptance — "the whitelist is checked at both moments" only holds if both call through
/// here rather than each growing its own copy of the range logic.
/// </summary>
public interface INodeVisibilityPolicy
{
    /// <summary>
    /// True if <paramref name="caller"/> is on this node's own local network, or matches a range the operator
    /// explicitly whitelisted. False for everything else — there is no "ask again", visibility is binary.
    /// </summary>
    Task<bool> IsAllowedAsync(IPAddress caller, CancellationToken cancellationToken = default);
}
