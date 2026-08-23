using Cockpit.Core.Mcp;

namespace Cockpit.Core.Abstractions.Mcp;

/// <summary>
/// A source of MCP servers the cockpit itself hosts on loopback (AC-40): session-status, orchestrator, a
/// plugin-mounted endpoint. Unlike a user-added server these never enter the operator's MCP-servers manager, only
/// answered live (cheap synchronous read, no network call) and merged into the fan-out by <see cref="IMcpServerCatalog"/>; each carries <see cref="McpServerConfig.CockpitHosted"/> true, so spawn paths hand it this run's auth key.
/// </summary>
public interface ICockpitInternalMcpProvider
{
    /// <summary>
    /// The cockpit-hosted servers available right now, or an empty list when none are running yet.
    /// </summary>
    IReadOnlyList<McpServerConfig> GetServers();

    /// <summary>
    /// This instance's live network-node addresses (AC-790), one per mounted endpoint that is currently reachable
    /// off-loopback, or empty when node binding is off. A cheap synchronous read of the host's current state, like
    /// <see cref="GetServers"/> — not a network call.
    /// </summary>
    IReadOnlyList<NodeEndpointAddress> GetNodeAddresses();
}
