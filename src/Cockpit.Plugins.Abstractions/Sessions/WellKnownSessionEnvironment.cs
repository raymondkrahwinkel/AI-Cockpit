namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// Environment variables the host sets on a spawned session that a plugin driver knows by name. Kept here so the host
/// and every provider plugin agree on the exact name without one hard-coding a string the other might mistype.
/// </summary>
public static class WellKnownSessionEnvironment
{
    /// <summary>
    /// The app-lifetime key guarding the cockpit's own loopback MCP endpoints (AC-40). The host sets it on every
    /// spawned session; a driver references it — rather than embedding a literal — in the <c>Authorization</c> header
    /// of a cockpit-hosted server (<see cref="PluginMcpServer.CockpitHosted"/>), e.g. <c>Bearer ${COCKPIT_MCP_KEY}</c>
    /// for Claude or <c>bearer_token_env_var</c> for Codex, so the key never lands in a config file on disk.
    /// </summary>
    public const string CockpitMcpKey = "COCKPIT_MCP_KEY";
}
