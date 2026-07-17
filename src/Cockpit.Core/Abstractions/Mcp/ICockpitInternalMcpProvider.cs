using Cockpit.Core.Mcp;

namespace Cockpit.Core.Abstractions.Mcp;

/// <summary>
/// A source of MCP servers the cockpit itself hosts on loopback (AC-40): the session-status endpoint, the
/// orchestrator, a plugin-mounted endpoint. Unlike a user-added server, these never belong in the MCP-servers
/// manager the operator edits — so rather than being published into <see cref="IMcpServerStore"/>, they are
/// answered live here and merged into the session fan-out by <see cref="IMcpServerCatalog"/>. That keeps the store
/// the operator's own registry, while a cockpit-hosted server still reaches every session that should have it.
/// </summary>
/// <remarks>
/// Asked each time a session's servers are gathered, so it must be a cheap synchronous read of what the host
/// currently has (its live loopback URL, its enabled state) — not a network call. Each server it returns carries
/// <see cref="McpServerConfig.CockpitHosted"/> true, so the spawn paths hand it this run's auth key.
/// </remarks>
public interface ICockpitInternalMcpProvider
{
    /// <summary>The cockpit-hosted servers available right now, or an empty list when none are running yet.</summary>
    IReadOnlyList<McpServerConfig> GetServers();
}
